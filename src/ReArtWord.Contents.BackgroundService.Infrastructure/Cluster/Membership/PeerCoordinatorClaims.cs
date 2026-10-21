using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;

/// <summary>受信した担当宣言の競合だけを収束させ、通常の単一担当を維持します。</summary>
internal sealed class PeerCoordinatorClaims
{
    private readonly Dictionary<string, PeerAnnouncement> announcements = new(StringComparer.Ordinal);
    private PeerInfo? reunionOwner;

    /// <summary>受理済みの通知を古い同実体の宣言で巻き戻しません。</summary>
    /// <returns>新しい順序として受理した場合だけtrueです。</returns>
    internal bool Observe(PeerAnnouncement announcement)
    {
        if (!announcement.IsNewerThan(announcements.GetValueOrDefault(announcement.Peer.PeerId)))
        {
            return false;
        }
        announcements[announcement.Peer.PeerId] = announcement;
        return true;
    }

    /// <summary>非同期の後処理に進む前に受理した通知の世代を確認します。</summary>
    /// <returns>同じ通知が現在も最新の場合だけtrueです。</returns>
    internal bool IsCurrent(PeerAnnouncement announcement) => announcements.TryGetValue(announcement.Peer.PeerId, out PeerAnnouncement? current)
        && current.Peer.InstanceId == announcement.Peer.InstanceId && current.Sequence == announcement.Sequence;

    /// <summary>停止した旧実体への復帰を許さず、同期中の同実体だけ判断を維持します。</summary>
    internal void ForgetUnavailable(IReadOnlyDictionary<string, PeerInfo> peers)
    {
        if (reunionOwner is not null && (!peers.TryGetValue(reunionOwner.PeerId, out PeerInfo? current) || current.InstanceId != reunionOwner.InstanceId || current.Status is EPeerStatus.Unavailable or EPeerStatus.Detached or EPeerStatus.Conflict))
        {
            reunionOwner = null;
        }
    }

    /// <summary>同順位の複数自己担当宣言を、共通前担当または連続参加順へ収束します。</summary>
    /// <returns>通常選出後に採用する担当です。</returns>
    internal string? Resolve(IReadOnlyDictionary<string, PeerInfo> peers, PeerInfo local, string? coordinator, string? previous)
    {
        ForgetUnavailable(peers);
        if (coordinator is null || !peers.TryGetValue(coordinator, out PeerInfo? current) || !PeerStatusRules.CanParticipate(current.Status))
        {
            return coordinator;
        }

        // 担当自身の移譲通知に、第三Peerも追従します。
        PeerAnnouncement? owner = announcements.GetValueOrDefault(current.PeerId);
        if (owner?.Peer.InstanceId == current.InstanceId && owner.Coordinator is not null && peers.TryGetValue(owner.Coordinator, out PeerInfo? successor)
            && PeerStatusRules.CanParticipate(successor.Status) && current.Priority <= successor.Priority)
        {
            coordinator = successor.PeerId;
            current = successor;
        }
        string selected = coordinator;
        PeerInfo[] contenders = peers.Values.Where(peer => PeerStatusRules.CanParticipate(peer.Status) && peer.Priority == current.Priority && ClaimsCoordinator(peer, local.InstanceId, selected))
            .OrderBy(peer => peer.ReadySince).ThenBy(peer => peer.PeerId, StringComparer.Ordinal).ToArray();
        if (1 < contenders.Length)
        {
            string? former = PreviousClaim(contenders[0], local.InstanceId, previous);
            PeerInfo? restored = contenders.FirstOrDefault(peer => string.Equals(peer.PeerId, former, StringComparison.Ordinal));
            if (restored is not null && contenders.All(peer => string.Equals(PreviousClaim(peer, local.InstanceId, previous), former, StringComparison.Ordinal)))
            {
                reunionOwner = restored;
                coordinator = restored.PeerId;
            }
            else
            {
                coordinator = contenders[0].PeerId;
            }
        }

        // 今回の再結合で確認した旧担当は、同実体のSyncingを越えて復帰できます。
        if (reunionOwner is not null && peers.TryGetValue(reunionOwner.PeerId, out PeerInfo? returning))
        {
            if (returning.Priority < current.Priority)
            {
                reunionOwner = null;
            }
            else
            {
                if (PeerStatusRules.CanParticipate(returning.Status))
                {
                    coordinator = returning.PeerId;
                }
            }
        }
        return coordinator;
    }

    /// <summary>全現実体から同じ担当宣言を受信したら、次の分断用に履歴を閉じます。</summary>
    /// <returns>欠損や同期中Peerを除外せず一致したかです。</returns>
    internal bool IsAgreed(IReadOnlyDictionary<string, PeerInfo> peers, Guid localInstance, string? coordinator) => peers.Values.Where(peer => peer.Status != EPeerStatus.Detached)
        .All(peer => PeerStatusRules.CanParticipate(peer.Status) && (peer.InstanceId == localInstance || (announcements.TryGetValue(peer.PeerId, out PeerAnnouncement? claim)
            && claim.Peer.InstanceId == peer.InstanceId && string.Equals(claim.Coordinator, coordinator, StringComparison.Ordinal))));

    /// <summary>同一実体が自分を担当と宣言した事実だけを比較します。</summary>
    /// <returns>現在の自己担当宣言があるかです。</returns>
    private bool ClaimsCoordinator(PeerInfo peer, Guid localInstance, string coordinator)
    {
        if (peer.InstanceId == localInstance)
        {
            return string.Equals(coordinator, peer.PeerId, StringComparison.Ordinal);
        }
        return announcements.TryGetValue(peer.PeerId, out PeerAnnouncement? claim) && claim.Peer.InstanceId == peer.InstanceId && string.Equals(claim.Coordinator, peer.PeerId, StringComparison.Ordinal);
    }

    /// <summary>自己担当宣言に添えられた分断前担当を参照します。</summary>
    /// <returns>確認された履歴、なければnullです。</returns>
    private string? PreviousClaim(PeerInfo peer, Guid localInstance, string? previous)
    {
        if (peer.InstanceId == localInstance)
        {
            return previous;
        }
        return announcements.GetValueOrDefault(peer.PeerId)?.PreviousCoordinator;
    }
}
