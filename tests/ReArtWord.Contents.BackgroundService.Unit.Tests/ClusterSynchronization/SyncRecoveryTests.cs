using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>相手・同期世代・再試行期限をまたいで未確認を誤解除しないことを検証します。</summary>
public sealed class SyncRecoveryTests
{
    /// <summary>版一致や準備中の応答だけでは初回確認を省略せず、旧実体の完了で新実体を確認済みにしません。</summary>
    [Fact]
    public void EachPeerInstanceRequiresItsOwnCompletedSynchronization()
    {
        SyncRecovery recovery = new(TimeProvider.System);
        PeerInfo peer = Peer("peer");
        Assert.True(recovery.NeedsSynchronization(peer));
        recovery.Failed(peer, "Sync.PendingOperations");
        Assert.True(recovery.NeedsSynchronization(peer));
        recovery.Confirmed(peer.PeerId, peer.InstanceId, recovery.Revision);
        Assert.False(recovery.NeedsSynchronization(peer));
        PeerInfo replacement = peer with { InstanceId = Guid.NewGuid() };
        Assert.True(recovery.NeedsSynchronization(replacement));
        long started = recovery.Revision;
        recovery.Failed(replacement, "Sync.HashMismatch");
        recovery.Confirmed(peer.PeerId, peer.InstanceId, started);
        Assert.True(recovery.NeedsSynchronization(replacement));
        Assert.Equal("Sync.HashMismatch", recovery.Problem);
        recovery.Confirmed(replacement.PeerId, replacement.InstanceId, recovery.Revision);
        Assert.False(recovery.NeedsSynchronization(replacement));
        Assert.Null(recovery.Problem);
        recovery.Failed(peer, "Sync.ChunkUnconfirmed");
        Assert.True(recovery.NeedsSynchronization(replacement));
        recovery.Confirmed(replacement.PeerId, replacement.InstanceId, recovery.Revision);
        Assert.Null(recovery.Problem);
    }

    /// <summary>未確定準備の能力応答を受けたら、履歴再書出しを抑え、準備の終了後に確認を再開します。</summary>
    [Fact]
    public void PendingPreparationsDeferTheNextProbeWithoutCreatingAPermanentSyncFailure()
    {
        CollaborationClock clock = new();
        SyncRecovery recovery = new(clock);
        PeerInfo peer = Peer("pending");
        recovery.Confirmed(peer.PeerId, peer.InstanceId, recovery.Revision);
        recovery.Failed(peer, "Sync.PendingOperations");
        Assert.False(recovery.CanSynchronize(peer));
        Assert.False(recovery.NeedsSynchronization(peer));
        Assert.Null(recovery.Problem);
        Assert.True(recovery.CanSynchronize(peer with { InstanceId = Guid.NewGuid() }));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(recovery.CanSynchronize(peer));
        recovery.Failed(peer, "Sync.PendingOperations");
        recovery.Confirmed(peer.PeerId, peer.InstanceId, recovery.Revision);
        Assert.True(recovery.CanSynchronize(peer));
    }

    /// <summary>通常保存中の準備を未確認障害へ変えず、既にある本当の障害は保持します。</summary>
    [Fact]
    public void PreparedWritesDoNotForceFullSynchronizationAfterVersionsMatch()
    {
        SyncRecovery recovery = new(TimeProvider.System);
        PeerInfo peer = Peer("peer");
        recovery.Confirmed(peer.PeerId, peer.InstanceId, recovery.Revision);
        recovery.Failed(peer, "Sync.PendingOperations");
        Assert.False(recovery.NeedsSynchronization(peer));
        Assert.Null(recovery.Problem);
        recovery.Failed(peer, "Sync.HashMismatch");
        recovery.Failed(peer, "Sync.PendingOperations");
        Assert.True(recovery.NeedsSynchronization(peer));
        Assert.Equal("Sync.HashMismatch", recovery.Problem);
    }

    /// <summary>他の相手の成功と開始後の失敗を別々に保持します。</summary>
    [Fact]
    public void ConfirmationOnlyResolvesTheSamePeerFailuresPredatingItsSession()
    {
        SyncRecovery recovery = new(TimeProvider.System);
        PeerInfo first = Peer("first");
        PeerInfo second = Peer("second");
        recovery.Failed(first, "Sync.ChunkUnconfirmed");
        long started = recovery.Revision;
        recovery.Confirmed(second.PeerId, second.InstanceId, started);
        Assert.True(recovery.NeedsSynchronization(first));
        recovery.Failed(first, "Sync.HashMismatch");
        recovery.Confirmed(first.PeerId, first.InstanceId, started);
        Assert.Equal("Sync.HashMismatch", recovery.Problem);
        recovery.Failed(second, "Sync.ResponseUnconfirmed");
        recovery.Confirmed(first.PeerId, first.InstanceId, recovery.Revision);
        Assert.Equal("Sync.ResponseUnconfirmed", recovery.Problem);
        Assert.False(recovery.NeedsSynchronization(first));
        recovery.Confirmed(second.PeerId, second.InstanceId, recovery.Revision);
        Assert.Null(recovery.Problem);
    }

    /// <summary>失敗時の書出し連打を制限し、新実体は古い待機を引き継ぎません。</summary>
    [Fact]
    public void RepeatedFailuresBackOffToThirtySecondsAndNewInstancesCanRecover()
    {
        CollaborationClock clock = new();
        SyncRecovery recovery = new(clock);
        PeerInfo peer = Peer("source");
        foreach (int seconds in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            recovery.Failed(peer, "Sync.ChunkUnconfirmed");
            Assert.True(recovery.NeedsSynchronization(peer));
            Assert.False(recovery.CanSynchronize(peer));
            clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
            Assert.False(recovery.CanSynchronize(peer));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.True(recovery.CanSynchronize(peer));
        }
        recovery.Failed(peer, "Sync.ChunkUnconfirmed");
        Assert.True(recovery.CanSynchronize(peer with { InstanceId = Guid.NewGuid() }));
        recovery.Confirmed(peer.PeerId, peer.InstanceId, recovery.Revision);
        recovery.Failed(peer, "Sync.Conflict");
        Assert.False(recovery.NeedsSynchronization(peer));
        Assert.True(recovery.NeedsSynchronization(peer, conflictsResolved: true));
        Assert.Equal("Sync.Conflict", recovery.Problem);
    }

    /// <summary>連続変更でも猶予を更新し続けず、一致と新しい実体だけが期限をリセットします。</summary>
    [Fact]
    public void ChangingVersionsHaveABoundedPerInstanceDeferral()
    {
        ProbeClock clock = new();
        SyncRecovery recovery = new(clock);
        PeerInfo first = Peer("first"), second = Peer("second");
        Assert.True(recovery.DeferChangingVersion(first, true));
        clock.MoveUtc(TimeSpan.FromHours(-1));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(recovery.DeferChangingVersion(first, true));
        Assert.True(recovery.DeferChangingVersion(second, true));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(recovery.DeferChangingVersion(first, true));
        Assert.True(recovery.DeferChangingVersion(second, true));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(recovery.DeferChangingVersion(first, true));
        Assert.True(recovery.DeferChangingVersion(first with { InstanceId = Guid.NewGuid() }, true));
        Assert.False(recovery.DeferChangingVersion(second, false));
        Assert.True(recovery.DeferChangingVersion(second, true));
        recovery.Confirmed(second.PeerId, second.InstanceId, recovery.Revision);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(recovery.DeferChangingVersion(second, true));
    }

    /// <summary>到達実体だけが異なる参加者を作ります。</summary>
    /// <returns>比較対象のPeerです。</returns>
    private static PeerInfo Peer(string id) => new(id, Guid.NewGuid(), id, "127.0.0.1", 47740, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>壁時計の補正と単調な経過時間を独立に進めます。</summary>
    private sealed class ProbeClock : TimeProvider
    {
        private DateTimeOffset utc = DateTimeOffset.UtcNow;
        private long ticks;

        /// <summary>経過時間はTimeSpanのtick単位です。</summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>壁時計です。</summary>
        /// <returns>補正を含む現在時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => utc;

        /// <summary>壁時計から独立した経過時間です。</summary>
        /// <returns>単調な時刻です。</returns>
        public override long GetTimestamp() => ticks;

        /// <summary>自然な時間経過を両時計へ反映します。</summary>
        internal void Advance(TimeSpan elapsed)
        {
            ticks += elapsed.Ticks;
            utc += elapsed;
        }

        /// <summary>経過時間を巻き戻さずに壁時計だけを補正します。</summary>
        internal void MoveUtc(TimeSpan difference) => utc += difference;
    }
}
