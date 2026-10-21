using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>実行時の成否だけを描画し、nullの表示でノードを実行しません。</summary>
internal sealed class EditorValueStates
{
    private static readonly IReadOnlySet<string> empty = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>現在の保存版で、完了が観測されていない処理のノードを一括取得します。</summary>
    /// <returns>並行する古い要求が実行中の場合も含む、実行中ノードの集合です。</returns>
    internal IReadOnlySet<string> RunningNodes(string? cluster, EditorDraft draft)
    {
        if (cluster is null || draft.Dirty || !clusters.TryGetValue(cluster, out NodeValueObservation[]? values))
        {
            return empty;
        }
        return values.Where(value => value.CompletedAt is null && string.Equals(value.WorkflowId, draft.Document.Id, StringComparison.Ordinal)
            && string.Equals(value.Revision, draft.BaseRevision, StringComparison.Ordinal)).Select(value => value.NodeId).ToHashSet(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, NodeValueObservation[]> clusters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> hashes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string? Cluster, string Workflow), (string Hash, int Version)> views = [];

    /// <summary>成否が変わった時だけキャンバスを更新します。</summary>
    internal int Version
    {
        get; private set;
    }

    /// <summary>他のWFの動作だけで、表示中の重いキャンバスを再描画しません。</summary>
    /// <returns>このWFの表示内容が変わった時だけ増える版です。</returns>
    internal int VersionFor(string? cluster, EditorDraft draft)
    {
        NodeValueObservation[] relevant = [];
        if (!draft.Dirty && cluster is not null && clusters.TryGetValue(cluster, out NodeValueObservation[]? values))
        {
            relevant = values.Where(value => string.Equals(value.WorkflowId, draft.Document.Id, StringComparison.Ordinal) && string.Equals(value.Revision, draft.BaseRevision, StringComparison.Ordinal)).ToArray();
        }
        string hash = ContractJson.Hash(relevant);
        (string? Cluster, string Workflow) key = (cluster, draft.Document.Id);
        if (views.TryGetValue(key, out (string Hash, int Version) old) && string.Equals(old.Hash, hash, StringComparison.Ordinal))
        {
            return old.Version;
        }
        if (128 <= views.Count && !views.ContainsKey(key))
        {
            views.Remove(views.Keys.First());
        }
        views[key] = (hash, ++Version);
        return Version;
    }

    /// <summary>既存の状態照会に含まれる観測だけを使います。</summary>
    internal void Update(string cluster, IEnumerable<NodeValueObservation> values)
    {
        NodeValueObservation[] snapshot = values.OrderByDescending(value => value.StartedAt).Take(4096).ToArray();
        string hash = ContractJson.Hash(snapshot);
        if (hashes.TryGetValue(cluster, out string? previous) && string.Equals(previous, hash, StringComparison.Ordinal))
        {
            return;
        }
        if (32 <= clusters.Count && !clusters.ContainsKey(cluster))
        {
            string removed = clusters.Keys.First();
            clusters.Remove(removed);
            hashes.Remove(removed);
        }
        clusters[cluster] = snapshot;
        hashes[cluster] = hash;
        Version++;
    }

    /// <summary>最新の同じ実行事象の対象だけをまとめ、別版の古い成功を表示しません。</summary>
    /// <returns>処理中・未確認null、確定成功true、確定失敗falseです。</returns>
    internal string Display(string? cluster, EditorDraft draft, string node)
    {
        if (cluster is null || draft.Dirty || !clusters.TryGetValue(cluster, out NodeValueObservation[]? values))
        {
            return "null";
        }
        NodeValueObservation[] matches = values.Where(value => string.Equals(value.WorkflowId, draft.Document.Id, StringComparison.Ordinal)
            && string.Equals(value.Revision, draft.BaseRevision, StringComparison.Ordinal) && string.Equals(value.NodeId, node, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
        {
            return "null";
        }
        NodeValueObservation[] current = matches.Where(value => value.CausationId == matches[0].CausationId).ToArray();
        if (current.Any(value => value.CompletedAt is null || value.Outcome is not (EOutcome.Succeeded or EOutcome.Failed)))
        {
            return "null";
        }
        return current.All(value => value.Outcome == EOutcome.Succeeded) ? "true"
                                                                         : "false";
    }
}
