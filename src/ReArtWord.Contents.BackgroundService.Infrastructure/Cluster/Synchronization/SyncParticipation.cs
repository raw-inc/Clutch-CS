using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>一回の同期と参加状態の境界を所有し、追従待ちで稼働中のPeerを停止させません。</summary>
internal sealed class SyncParticipation(PeerCluster cluster, PeerMembership membership, PeerRecovery recovery, RepositorySynchronization repositorySync, IDurableRepository repository) : IDisposable
{
    private readonly SemaphoreSlim synchronization = new(1, 1);

    /// <summary>最後に確認した現在状態の版です。</summary>
    internal string Digest
    {
        get; set;
    } = string.Empty;

    /// <summary>受理した通常参加通知を基に、同期が必要な場合だけ相手へ接続します。</summary>
    /// <returns>必要な同期の完了です。</returns>
    internal async Task ObserveReadyAsync(PeerAnnouncement announcement, PeerInfo? previous, CancellationToken cancellationToken = default)
    {
        EPeerStatus localStatus = cluster.GetPeers().Single(peer => peer.InstanceId == cluster.LocalInstanceId).Status;
        bool participating = PeerStatusRules.CanParticipate(localStatus);
        bool conflictsResolved = localStatus == EPeerStatus.Ready && announcement.Peer.Status == EPeerStatus.Ready;
        if (!recovery.Detached && PeerStatusRules.CanParticipate(announcement.Peer.Status) && (!string.Equals(announcement.Digest, Digest, StringComparison.Ordinal) || repositorySync.NeedsSynchronization(announcement.Peer, conflictsResolved) || !participating))
        {
            bool mustGate = previous?.Status == EPeerStatus.Unavailable || !participating;
            await SynchronizeAsync(announcement.Peer, mustGate, conflictsResolved && previous?.InstanceId == announcement.Peer.InstanceId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>双方の稼働基準を確認後にReadyへ進め、対応する過去本文は独立して補完します。</summary>
    /// <returns>同期の完了。</returns>
    internal async Task SynchronizeAsync(PeerInfo peer, bool mustGate, bool compareCurrent, CancellationToken cancellationToken = default)
    {
        if (!await synchronization.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            if (!repositorySync.CanSynchronize(peer))
            {
                return;
            }
            if (!mustGate && compareCurrent && !repositorySync.NeedsSynchronization(peer, conflictsResolved: true)
                && await repositorySync.CompareVersionAsync(peer, cancellationToken).ConfigureAwait(false) is { RequiresSynchronization: false } compared)
            {
                Digest = compared.MatchingVersion ?? Digest;
                return;
            }
            bool alreadyParticipating = cluster.GetPeers().Any(item => item.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(item.Status));
            if (mustGate && (!alreadyParticipating || !await repositorySync.SupportsStagedAsync(peer, cancellationToken).ConfigureAwait(false)))
            {
                membership.SetLocal(EPeerStatus.Syncing, preserveConflict: true);
            }
            if (await repositorySync.SynchronizeAsync(peer, cancellationToken).ConfigureAwait(false))
            {
                Digest = await repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false);
                await recovery.BecomeReadyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            synchronization.Release();
        }
    }

    /// <summary>参加判定の排他を解放します。</summary>
    public void Dispose() => synchronization.Dispose();
}
