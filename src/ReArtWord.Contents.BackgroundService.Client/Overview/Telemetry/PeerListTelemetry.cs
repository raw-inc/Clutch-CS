using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;

/// <summary>接続選択を変えず、一覧の件数を制限したページに必要な稼働確認だけを取得します。</summary>
internal sealed class PeerListTelemetry(ManagementApi api)
{
    private readonly List<PeerListRow> rows = [];

    /// <summary>表示するPC・クラスタ・Peerの組と今回の実確認です。</summary>
    internal IReadOnlyList<PeerListRow> Rows => rows;

    /// <summary>発見したPeerの全件数です。</summary>
    internal int Total
    {
        get; private set;
    }

    /// <summary>今回表示するページの先頭です。</summary>
    internal int Offset
    {
        get; private set;
    }

    /// <summary>発見自体の失敗を空一覧と区別します。</summary>
    internal string? Problem
    {
        get; private set;
    }

    /// <summary>全対象を確認できるよう次の件数を制限したページへ移動します。</summary>
    internal void NextPage()
    {
        Offset += DashboardTelemetry.MaximumPeers;
        if (Total <= Offset)
        {
            Offset = 0;
        }
    }

    /// <summary>履歴・ログ・実行一覧を取得せず、現在の最大128Peerを一括で照会します。</summary>
    /// <returns>選択先と独立した一巡の状態確認です。</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            IReadOnlyList<DiscoveredCluster> discovered = await api.GetAsync<List<DiscoveredCluster>>("/client/clusters", timeout.Token).ConfigureAwait(false) ?? [];
            PeerListRow[] targets = discovered.SelectMany(cluster => cluster.Peers.Select(peer => new PeerListRow(cluster.ClusterId, peer, null)))
                .OrderBy(row => row.Peer.HostId, StringComparer.Ordinal).ThenBy(row => row.Cluster, StringComparer.Ordinal)
                .ThenBy(row => row.Peer.PeerId, StringComparer.Ordinal).ThenBy(row => row.Peer.InstanceId).ToArray();
            Total = targets.Length;
            if (Total <= Offset)
            {
                Offset = 0;
            }
            PeerListRow[] page = targets.Skip(Offset).Take(DashboardTelemetry.MaximumPeers).ToArray();
            DashboardSnapshotQuery[] queries = page.Select(row => new DashboardSnapshotQuery(row.Cluster, row.Peer.PeerId, row.Peer.InstanceId, HealthOnly: true)).ToArray();
            IReadOnlyList<DashboardPeerSnapshot> snapshots = await api.PostAsync<List<DashboardPeerSnapshot>>("/client/dashboard/batch", queries, timeout.Token).ConfigureAwait(false) ?? [];
            DateTimeOffset now = api.UtcNow;
            for (int index = 0; index < page.Length; index++)
            {
                PeerListRow row = page[index];
                DashboardPeerSnapshot? snapshot = snapshots.FirstOrDefault(item => string.Equals(item.ClusterId, row.Cluster, StringComparison.Ordinal)
                    && string.Equals(item.Peer.PeerId, row.Peer.PeerId, StringComparison.Ordinal) && item.Peer.InstanceId == row.Peer.InstanceId);
                PeerHealth? health = snapshot?.Health?.Deserialize<PeerHealth>(Contracts.Serialization.ContractJson.Options);
                if (health is not null && health.InstanceId == row.Peer.InstanceId && string.Equals(health.PeerId, row.Peer.PeerId, StringComparison.Ordinal)
                    && string.Equals(health.ClusterId, row.Cluster, StringComparison.Ordinal) && snapshot?.CheckedAt is { } checkedAt
                    && checkedAt <= now && now - checkedAt <= TimeSpan.FromSeconds(15)
                    && row.Peer.LastSeen <= now && now - row.Peer.LastSeen <= TimeSpan.FromSeconds(15))
                {
                    page[index] = row with { Health = health };
                }
            }
            rows.Clear();
            rows.AddRange(page);
            Problem = null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            Problem = "Peer discovery or status is unconfirmed";
            for (int index = 0; index < rows.Count; index++)
            {
                rows[index] = rows[index] with { Health = null };
            }
        }
    }
}

/// <summary>実体が一致した稼働確認だけを版の根拠として表示します。</summary>
internal sealed record PeerListRow(string Cluster, PeerInfo Peer, PeerHealth? Health);
