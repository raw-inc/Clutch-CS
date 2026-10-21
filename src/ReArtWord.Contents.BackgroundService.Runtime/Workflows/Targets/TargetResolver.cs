using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

/// <summary>指定外のPeerへ置換せずに実行対象集合を確定します。</summary>
public sealed class TargetResolver
{
    private readonly IPeerDirectory directory;

    /// <summary>確認済み参加情報から対象を解決します。</summary>
    public TargetResolver(IPeerDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        this.directory = directory;
    }

    /// <summary>Peer・Host Group・Coordinator指定を一つの対象集合へ解決します。</summary>
    /// <returns>開始時の対象と、不成立の場合の理由です。</returns>
    public TargetResolution Resolve(NodeDefinition node, NodeDescriptor descriptor, PeerInfo? sourcePeer = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? hostGroups = null, ClusterOperationalSnapshot? operational = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(descriptor);
        IReadOnlyList<PeerInfo> known = directory.GetPeers();
        bool detached = known.Any(peer => peer.InstanceId == directory.LocalInstanceId && peer.Status == EPeerStatus.Detached);
        TargetDefinition? target = node.Target;
        if (target?.SourcePeer == true)
        {
            if (sourcePeer is null)
            {
                return Missing("target.sourceMissing", "この実行には入力発生元Peerの取得時情報がありません。");
            }
            if (detached && !string.Equals(sourcePeer.PeerId, directory.LocalPeerId, StringComparison.Ordinal))
            {
                return Missing("target.detached", "明示離脱中は別Peerの処理へ配送しません。");
            }
            return new TargetResolution(EExecutionScope.One, [sourcePeer]);
        }
        if (target is null)
        {
            string? coordinator = directory.CoordinatorId;
            PeerInfo? local = known.FirstOrDefault(peer => string.Equals(peer.PeerId, directory.LocalPeerId, StringComparison.Ordinal));
            if (local?.Status == EPeerStatus.Detached)
            {
                coordinator = directory.LocalPeerId;
            }
            if (coordinator is null)
            {
                return Missing("coordinator.unavailable", "実行可能なCoordinatorを確認できません。");
            }
            target = new TargetDefinition(EExecutionScope.One, [coordinator]);
        }

        HashSet<string> identities = new(target.Peers ?? [], StringComparer.Ordinal);
        if (target.Groups is not null)
        {
            string? problem = OperationalDependencyPolicy.GetProblem(operational, node, descriptor);
            if (problem is not null)
            {
                return Missing(problem, "この一件に固定された共通設定の未解消部分で対象を選択できません。");
            }
            IReadOnlyDictionary<string, IReadOnlyList<string>> groups = hostGroups ?? directory.GetHostGroups();
            foreach (string group in target.Groups)
            {
                if (!groups.TryGetValue(group, out IReadOnlyList<string>? hosts))
                {
                    return Missing("target.group", "指定したHost Groupを確認できません。");
                }
                if (target.Scope == EExecutionScope.All && hosts.Any(host => !known.Any(peer => string.Equals(peer.HostId, host, StringComparison.Ordinal))))
                {
                    return Missing("target.hostMissing", "All対象のHostに属するPeerをまだ確認できません。対象が不明なまま残りだけを実行しません。");
                }
                identities.UnionWith(known.Where(peer => hosts.Contains(peer.HostId, StringComparer.Ordinal)).Select(static peer => peer.PeerId));
            }
        }
        if (identities.Count == 0)
        {
            return Missing("target.empty", "対象集合にPeerがありません。");
        }
        if (detached && (!identities.Contains(directory.LocalPeerId) || (target.Scope != EExecutionScope.Any && identities.Count != 1)))
        {
            return Missing("target.detached", "明示離脱中は対象を局所Peerへ明示してください。");
        }
        List<PeerInfo> selected = [];
        foreach (string identity in identities.Order(StringComparer.Ordinal))
        {
            PeerInfo? peer = known.FirstOrDefault(candidate => string.Equals(candidate.PeerId, identity, StringComparison.Ordinal));
            peer ??= new PeerInfo(identity, Guid.Empty, string.Empty, string.Empty, 0, EPeerStatus.Unavailable, 0, null, DateTimeOffset.MinValue);
            selected.Add(peer);
        }
        if (target.Scope == EExecutionScope.Any)
        {
            PeerInfo? eligible = selected.Where(peer => (detached && peer.InstanceId == directory.LocalInstanceId && peer.Status == EPeerStatus.Detached)
                || (!detached && PeerStatusRules.CanParticipate(peer.Status))).OrderByDescending(peer => peer.Priority)
                .ThenBy(peer => peer.ReadySince).ThenBy(peer => peer.PeerId, StringComparer.Ordinal).FirstOrDefault();
            if (eligible is null)
            {
                return Missing("target.unavailable", "指定集合に実行可能なPeerがありません。");
            }
            return new TargetResolution(target.Scope, [eligible]);
        }
        return new TargetResolution(target.Scope, selected.AsReadOnly());
    }

    /// <summary>対象が成立しない結果を返します。</summary>
    /// <returns>副作用を開始しない対象解決結果です。</returns>
    private static TargetResolution Missing(string code, string message)
    {
        return new TargetResolution(EExecutionScope.One, [], NodeResult.Failure(code, message, EOutcome.NotExecuted));
    }
}
