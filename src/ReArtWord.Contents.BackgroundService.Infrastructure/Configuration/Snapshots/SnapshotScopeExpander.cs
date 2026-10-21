using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>選択時点のHost所属とノード対象を明示的な復元範囲へ展開します。</summary>
internal sealed class SnapshotScopeExpander(WorkflowStore workflows, PeerCluster cluster)
{
    /// <summary>未知の指定を除外して範囲を縮小することなく確認します。</summary>
    /// <returns>固定できた対象または指定の問題です。</returns>
    public async Task<SnapshotScopeResult> ExpandAsync(SnapshotScopeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        StoredWorkflow? saved = await workflows.GetAsync(request.WorkflowId, cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            return Problem("snapshot.workflowMissing", "対象の保存設定がありません。");
        }
        PeerInfo[] peers = cluster.GetPeers().ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<string>> groups = cluster.GetHostGroups();
        HashSet<string> hosts = new(request.HostIds ?? [], StringComparer.Ordinal);
        foreach (string group in request.HostGroups ?? [])
        {
            if (!groups.TryGetValue(group, out IReadOnlyList<string>? members))
            {
                return Problem("snapshot.groupMissing", "指定したHost Groupがありません。");
            }
            hosts.UnionWith(members);
        }
        if (hosts.Any(host => !peers.Any(peer => string.Equals(peer.HostId, host, StringComparison.Ordinal)))
            || (request.PeerIds ?? []).Any(id => !peers.Any(peer => string.Equals(peer.PeerId, id, StringComparison.Ordinal))))
        {
            return Problem("snapshot.peerMissing", "指定範囲のPeerまたはHostを確認できません。");
        }
        bool scoped = (request.PeerIds?.Count ?? 0) != 0 || (request.HostIds?.Count ?? 0) != 0 || (request.HostGroups?.Count ?? 0) != 0;
        PeerInfo[] selected = peers.Where(peer => !scoped || (request.PeerIds ?? []).Contains(peer.PeerId, StringComparer.Ordinal) || hosts.Contains(peer.HostId)).ToArray();
        if (selected.Length == 0 || selected.Any(peer => !PeerStatusRules.CanParticipate(peer.Status)))
        {
            return Problem("snapshot.peerUnavailable", "選択範囲の全Peerが通常参加するまで範囲を確定できません。");
        }
        if (request.NodeIds is not null && (request.NodeIds.Count == 0 || request.NodeIds.Any(id => !saved.Definition.Nodes.Any(node => string.Equals(node.Id, id, StringComparison.Ordinal)))))
        {
            return Problem("snapshot.nodeMissing", "選択Nodeが空または保存設定に存在しません。");
        }
        List<SnapshotTarget> targets = [];
        foreach (PeerInfo peer in selected.OrderBy(peer => peer.PeerId, StringComparer.Ordinal))
        {
            IReadOnlyList<string>? nodes = request.NodeIds;
            if (scoped)
            {
                nodes = saved.Definition.Nodes.Where(node => (nodes is null || nodes.Contains(node.Id, StringComparer.Ordinal)) && Belongs(node, peer, groups)).Select(node => node.Id).Order(StringComparer.Ordinal).ToArray();
                if (nodes.Count == 0)
                {
                    continue;
                }
            }
            targets.Add(new SnapshotTarget(request.WorkflowId, peer.PeerId, ESnapshotKind.Configuration, nodes?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        }
        return new SnapshotScopeResult(targets, []);
    }

    /// <summary>動的sourcePeerは実行時に決まるためノード指定を暗黙に固定しません。</summary>
    /// <returns>設定上このPeerを実行候補に含むか。</returns>
    private bool Belongs(NodeDefinition node, PeerInfo peer, IReadOnlyDictionary<string, IReadOnlyList<string>> groups)
    {
        if (node.Target is null)
        {
            return string.Equals(peer.PeerId, cluster.CoordinatorId, StringComparison.Ordinal);
        }
        return node.Target.SourcePeer || (node.Target.Peers ?? []).Contains(peer.PeerId, StringComparer.Ordinal)
            || (node.Target.Groups ?? []).Any(group => groups.TryGetValue(group, out IReadOnlyList<string>? hosts) && hosts.Contains(peer.HostId, StringComparer.Ordinal));
    }

    /// <summary>不明な範囲を空の成功にしません。</summary>
    /// <returns>範囲指定の問題です。</returns>
    private static SnapshotScopeResult Problem(string code, string message) => new([], [new ValidationIssue(code, message)]);
}
