using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;

/// <summary>概要に表示するWFだけの名前を、本文を取得せずクラスタ・Peerごとに短時間保持します。</summary>
internal sealed class OverviewWorkflowNames(ManagementApi api)
{
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private (string Cluster, string Peer)? owner;
    private DateTimeOffset refreshAfter;
    private int generation;

    /// <summary>削除済み・未取得のWFだけ識別子へ戻します。</summary>
    /// <returns>確認できた表示名です。</returns>
    internal string Name(string id) => names.GetValueOrDefault(id, id);

    /// <summary>可視範囲の有限件数をまとめて取得し、別対象の遅い返信を破棄します。</summary>
    /// <returns>名前の解決または既存表示の維持です。</returns>
    internal async Task RefreshAsync(string cluster, string peer, IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        if (owner != (cluster, peer))
        {
            names.Clear();
            refreshAfter = default;
            owner = (cluster, peer);
            generation++;
        }
        string[] requested = ids.Where(id => id.Length <= 256).Distinct(StringComparer.Ordinal).Take(32).ToArray();
        if (requested.Length == 0 || (api.UtcNow < refreshAfter && requested.All(names.ContainsKey)))
        {
            return;
        }
        int current = ++generation;
        WorkflowListPage? page = null;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            page = await api.PostForPeerAsync<WorkflowListPage>("/api/workflows/query", new WorkflowListQuery(32, MaximumBytes: 131072, Ids: requested), peer, cluster, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            // 旧Coreや欠測時もログ・活動を隠さず、未取得の名前だけ識別子で表示します。
        }
        if (current != generation || owner != (cluster, peer))
        {
            return;
        }
        Dictionary<string, string> observed = page?.Entries.Where(item => item.Id is not null).ToDictionary(item => item.Id!, item => item.Name, StringComparer.Ordinal) ?? [];
        foreach (string id in names.Keys.Except(requested, StringComparer.Ordinal).ToArray())
        {
            names.Remove(id);
        }
        foreach (string id in requested)
        {
            names[id] = observed.GetValueOrDefault(id) ?? names.GetValueOrDefault(id, id);
        }
        refreshAfter = api.UtcNow.AddSeconds(30);
    }
}
