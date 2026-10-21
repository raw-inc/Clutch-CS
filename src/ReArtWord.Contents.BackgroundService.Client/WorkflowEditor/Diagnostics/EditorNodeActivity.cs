using System.Collections.Concurrent;
using System.Collections.Frozen;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>処理中ノードの発光が有効な間、値を含まない実行状態を対象WFから取得します。</summary>
internal sealed class EditorNodeActivity(ManagementApi api)
{
    private readonly ConcurrentDictionary<(string Peer, Guid Instance), Observation> observations = new();
    private (string? Cluster, string? Workflow, string? Revision) scope;
    private DateTimeOffset nextRead;
    private bool busy;
    private int generation;
    private WorkflowDefinition? eligibilityDocument, eligibilityBase;
    private readonly HashSet<string> eligible = new(StringComparer.Ordinal);
    private readonly HashSet<string> groups = new(StringComparer.Ordinal);

    // 抑制理由: 読み手が保持する旧集合を変更せず、観測済みの不変集合を一度に交換します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "実行状態は不変のFrozenSetを交換し、描画中の集合を書き換えません。")]
    private FrozenSet<string> running = FrozenSet<string>.Empty;

    /// <summary>枠の変化だけを表示層へ通知します。</summary>
    internal event Action? Changed;

    /// <summary>同じ保存版で、現在処理中と確認したノードだけを返します。</summary>
    /// <returns>対象ノードが処理中かです。</returns>
    internal bool IsRunning(string? cluster, EditorDraft draft, string node)
    {
        if (SubworkflowInterface.IsSubworkflow(draft.Document) || scope != (cluster, draft.Document.Id, draft.BaseRevision))
        {
            return false;
        }
        if (!ReferenceEquals(eligibilityDocument, draft.Document) || !ReferenceEquals(eligibilityBase, draft.Base))
        {
            eligibilityDocument = draft.Document;
            eligibilityBase = draft.Base;
            eligible.Clear();
            groups.Clear();
            Dictionary<string, NodeDefinition> saved = draft.Base.Nodes.ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (NodeDefinition item in draft.Document.Nodes)
            {
                if (saved.TryGetValue(item.Id, out NodeDefinition? original) && (ReferenceEquals(item, original)
                    || string.Equals(ContractJson.Hash(item with { Position = null, Size = null, LogicalName = null }), ContractJson.Hash(original with { Position = null, Size = null, LogicalName = null }), StringComparison.Ordinal)))
                {
                    eligible.Add(item.Id);
                    if (SubworkflowInterface.Target(item.Type) is not null)
                    {
                        groups.Add(item.Id);
                    }
                }
            }
        }
        return eligible.Contains(node) && (running.Contains(node) || groups.Contains(node) && running.Any(id => RecipeNodeIdentity.Contains(node, id)));
    }

    /// <summary>画面・版・接続先の変更時は古い発光を解除します。</summary>
    internal void Reset()
    {
        generation++;
        scope = default;
        observations.Clear();
        eligibilityDocument = eligibilityBase = null;
        eligible.Clear();
        groups.Clear();
        nextRead = default;
        if (running.Count != 0)
        {
            running = FrozenSet<string>.Empty;
            Changed?.Invoke();
        }
    }

    /// <summary>並列数と期限を制限し、停止した観測を処理中として残しません。</summary>
    /// <returns>選択WFだけの現在状態です。</returns>
    internal async Task RefreshAsync(string? cluster, EditorDraft draft, IReadOnlyList<PeerInfo> peers, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled || cluster is null || draft.BaseRevision is null || SubworkflowInterface.IsSubworkflow(draft.Document))
        {
            Reset();
            return;
        }
        (string?, string?, string?) target = (cluster, draft.Document.Id, draft.BaseRevision);
        if (scope != target)
        {
            Reset();
            scope = target;
        }
        if (busy || api.UtcNow < nextRead)
        {
            return;
        }
        busy = true;
        int version = generation;
        nextRead = api.UtcNow.AddMilliseconds(500);
        PeerInfo[] targets = peers.Where(peer => PeerStatusRules.CanParticipate(peer.Status)).DistinctBy(peer => peer.PeerId).Take(256).ToArray();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (peer, token) =>
            {
                try
                {
                    WorkflowNodeActivity? value = await api.GetForPeerAsync<WorkflowNodeActivity>("/api/workflows/" + Uri.EscapeDataString(draft.Document.Id) + "/activity", peer.PeerId, cluster, token).ConfigureAwait(false);
                    if (version == generation && value is not null && value.InstanceId == peer.InstanceId
                        && string.Equals(value.PeerId, peer.PeerId, StringComparison.Ordinal) && string.Equals(value.WorkflowId, draft.Document.Id, StringComparison.Ordinal))
                    {
                        string[] nodes = value.Nodes.Where(node => node.CompletedAt is null && string.Equals(node.Revision, draft.BaseRevision, StringComparison.Ordinal))
                            .Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Take(1024).ToArray();
                        observations[(peer.PeerId, peer.InstanceId)] = new(nodes, api.UtcNow);
                    }
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
                {
                    // 取得不能な状態は短い期限で退場させます。
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            busy = false;
            if (version == generation)
            {
                DateTimeOffset now = api.UtcNow;
                foreach (KeyValuePair<(string Peer, Guid Instance), Observation> entry in observations)
                {
                    if (TimeSpan.FromSeconds(2) < now - entry.Value.At || !targets.Any(peer => string.Equals(peer.PeerId, entry.Key.Peer, StringComparison.Ordinal) && peer.InstanceId == entry.Key.Instance))
                    {
                        observations.TryRemove(entry.Key, out _);
                    }
                }
                FrozenSet<string> next = observations.Values.SelectMany(value => value.Nodes).ToFrozenSet(StringComparer.Ordinal);
                if (!running.SetEquals(next))
                {
                    running = next;
                    Changed?.Invoke();
                }
            }
        }
    }

    /// <summary>入力・出力の実値は保持しません。</summary>
    private sealed record Observation(IReadOnlyList<string> Nodes, DateTimeOffset At);
}
