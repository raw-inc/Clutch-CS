using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>クリック前に表示する候補を選び、曖昧なPeerを自動確定しません。</summary>
internal static class EditorClusterChoice
{
    /// <summary>前回の明示Peer、未選択なら単一Ready Coordinatorを候補として提示します。</summary>
    /// <returns>利用者がクリックして確定できる候補、または明示選択待ちです。</returns>
    internal static PeerInfo? Suggest(DiscoveredCluster cluster, string? previous)
    {
        PeerInfo[] unique = cluster.Peers.GroupBy(peer => peer.PeerId, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        if (!string.IsNullOrEmpty(previous))
        {
            return unique.FirstOrDefault(peer => string.Equals(peer.PeerId, previous, StringComparison.Ordinal) && peer.Status != EPeerStatus.Unavailable);
        }
        if (cluster.CoordinatorCandidates.Count == 1)
        {
            PeerInfo? coordinator = unique.FirstOrDefault(peer => string.Equals(peer.PeerId, cluster.CoordinatorCandidates[0], StringComparison.Ordinal) && peer.Status == EPeerStatus.Ready);
            if (coordinator is not null)
            {
                return coordinator;
            }
        }
        return null;
    }

    /// <summary>新旧実体の混在や同じIDの複数候補を受け入れません。</summary>
    /// <returns>現在観測がクリック時の実体と一意に一致する場合だけtrueです。</returns>
    internal static bool IsSameInstance(IReadOnlyList<DiscoveredCluster> observed, string cluster, string peer, Guid instance)
    {
        PeerInfo[] matches = observed.Where(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal)).SelectMany(item => item.Peers).Where(item => string.Equals(item.PeerId, peer, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 && matches[0].InstanceId == instance;
    }
}
