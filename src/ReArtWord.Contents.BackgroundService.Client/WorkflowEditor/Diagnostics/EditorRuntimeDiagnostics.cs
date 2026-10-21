using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>既存のDashboard観測キャッシュから待受・自動実行の問題を編集画面へ渡します。</summary>
internal sealed class EditorRuntimeDiagnostics(ManagementApi api, EditorDiagnostics diagnostics)
{
    private DateTimeOffset nextRead;

    /// <summary>Coreへの追加ポーリングを行わず、既存の最大256実体のキャッシュを照会します。</summary>
    /// <returns>観測済みの成否の反映です。未確認は成功として上書きしません。</returns>
    internal async Task RefreshAsync(string cluster, IReadOnlyList<PeerInfo> peers, CancellationToken cancellationToken = default)
    {
        if (api.UtcNow < nextRead)
        {
            return;
        }
        nextRead = api.UtcNow.AddSeconds(3);
        List<DashboardPeerSnapshot> snapshots = [];
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (PeerInfo[] chunk in peers.DistinctBy(peer => (peer.PeerId, peer.InstanceId)).Take(256).Chunk(128))
            {
                DashboardSnapshotQuery[] queries = chunk.Select(peer => new DashboardSnapshotQuery(cluster, peer.PeerId, peer.InstanceId, DiagnosticsOnly: true)).ToArray();
                snapshots.AddRange(await api.PostAsync<List<DashboardPeerSnapshot>>("/client/dashboard/batch", queries, timeout.Token).ConfigureAwait(true) ?? []);
            }
            if (!string.Equals(api.SelectedClusterId, cluster, StringComparison.Ordinal))
            {
                return;
            }
            DashboardPeerSnapshot[] fresh = snapshots.Where(snapshot => snapshot.Problem is null && snapshot.CheckedAt is { } at && at <= api.UtcNow && api.UtcNow - at < TimeSpan.FromSeconds(15)).ToArray();
            if (fresh.All(snapshot => snapshot.Status.HasValue) && fresh.Length == snapshots.Count && fresh.Length == peers.DistinctBy(peer => (peer.PeerId, peer.InstanceId)).Take(256).Count())
            {
                ServiceStatus[] statuses = fresh.Select(snapshot => ManagementApi.ReadResponse<ServiceStatus>(snapshot.Status!.Value)).OfType<ServiceStatus>().ToArray();
                diagnostics.SetSources(cluster, statuses.SelectMany(status => status.Sources).ToArray());
                diagnostics.Values.Update(cluster, statuses.SelectMany(status => status.NodeValues ?? []));
            }
            diagnostics.SetHistory(cluster, fresh.SelectMany(snapshot => snapshot.NodeExecutions ?? []).ToArray());
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            // 最後に確認した問題は保持し、通信失敗をノードの成功や新たなノード失敗へ置き換えません。
        }
    }

}
