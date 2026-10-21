using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>現担当維持と分断両側の担当宣言の収束を区別します。</summary>
public sealed class PeerCoordinatorElectionTests
{
    /// <summary>同順位の単一担当を、相手の古い参加時刻や分断履歴で退位させません。</summary>
    [Fact]
    public void SingleOwnerSurvivesEarlierFollowerAndStaleHistory()
    {
        ElectionClock clock = new();
        PeerMembership first = Create("a", clock);
        first.SetLocal(EPeerStatus.Ready);
        PeerInfo follower = new("b", Guid.NewGuid(), "b", "127.0.0.1", 12345, EPeerStatus.Ready, 1, clock.GetUtcNow().AddHours(-1), clock.GetUtcNow());
        for (int index = 0; index < 10; index++)
        {
            Assert.True(first.Observe(new(follower, "same", "a", "b", index + 1), follower.Address));
            Assert.Equal("a", first.Coordinator);
        }
    }

    /// <summary>両側の異なる前担当履歴があっても、自己担当宣言は一つへ収束します。</summary>
    [Fact]
    public void CompetingOwnersWithDifferentHistoryConvergeAndStayStable()
    {
        ElectionClock clock = new();
        PeerMembership first = Create("a", clock);
        PeerMembership second = Create("b", clock);
        second.SetLocal(EPeerStatus.Ready);
        clock.Advance(TimeSpan.FromSeconds(1));
        first.SetLocal(EPeerStatus.Ready);
        RememberExpiredPeer(first, clock);
        RememberExpiredPeer(second, clock);
        Assert.Equal("a", first.PreviousCoordinator);
        Assert.Equal("b", second.PreviousCoordinator);
        for (int index = 0; index < 10; index++)
        {
            Exchange(first, second);
            Assert.Equal("b", first.Coordinator);
            Assert.Equal("b", second.Coordinator);
        }
    }

    /// <summary>両側が確認した分断前担当は、参加時刻の早い臨時担当より優先します。</summary>
    [Fact]
    public void RejoinedOwnersRestoreTheirCommonFormerOwner()
    {
        ElectionClock firstClock = new();
        ElectionClock secondClock = new(firstClock.GetUtcNow().AddHours(-1));
        PeerMembership first = Create("a", firstClock);
        PeerMembership second = Create("b", secondClock);
        first.SetLocal(EPeerStatus.Ready);
        Assert.True(second.Observe(Announcement(first), "127.0.0.1"));
        second.SetLocal(EPeerStatus.Ready);
        Exchange(first, second);
        Assert.Equal("a", second.Coordinator);
        firstClock.Advance(TimeSpan.FromSeconds(10));
        secondClock.Advance(TimeSpan.FromSeconds(10));
        first.Expire();
        second.Expire();
        Assert.Equal("a", first.PreviousCoordinator);
        Assert.Equal("a", second.PreviousCoordinator);
        Assert.Equal("b", second.Coordinator);
        for (int index = 0; index < 10; index++)
        {
            Exchange(first, second);
            Assert.Equal("a", first.Coordinator);
            Assert.Equal("a", second.Coordinator);
        }
        Assert.Null(first.PreviousCoordinator);
        Assert.Null(second.PreviousCoordinator);
        first.SetLocal(EPeerStatus.Syncing);
        Exchange(first, second);
        Assert.Equal("b", second.Coordinator);
        first.SetLocal(EPeerStatus.Ready);
        for (int index = 0; index < 3; index++)
        {
            Exchange(first, second);
            Assert.Equal("a", first.Coordinator);
            Assert.Equal("a", second.Coordinator);
        }
        first.SetLocal(EPeerStatus.Unavailable);
        Exchange(first, second);
        Assert.Equal("b", second.Coordinator);
        first.SetLocal(EPeerStatus.Ready);
        Exchange(first, second);
        Assert.Equal("b", first.Coordinator);
        Assert.Equal("b", second.Coordinator);
    }

    /// <summary>担当自身の移譲通知を受けた第三Peerも旧担当へ固着しません。</summary>
    [Fact]
    public void FollowerTracksOwnerRelinquishmentButNotUnrelatedClaim()
    {
        ElectionClock clock = new();
        PeerMembership follower = Create("follower", clock);
        PeerInfo first = new("a", Guid.NewGuid(), "a", "127.0.0.1", 12345, EPeerStatus.Ready, 1, clock.GetUtcNow(), clock.GetUtcNow());
        PeerInfo second = first with { PeerId = "b", InstanceId = Guid.NewGuid(), ReadySince = clock.GetUtcNow().AddMinutes(1) };
        Assert.True(follower.Observe(new(first, "same", "a", null, 1), first.Address));
        follower.SetLocal(EPeerStatus.Ready);
        Assert.True(follower.Observe(new(second, "same", "a", "b", 1), second.Address));
        Assert.Equal("a", follower.Coordinator);
        Assert.True(follower.Observe(new(first, "same", "b", null, 2), first.Address));
        Assert.Equal("b", follower.Coordinator);
    }

    /// <summary>参加時刻も同じ担当候補はPeerId順で一意になります。</summary>
    [Fact]
    public void EqualReadyTimesUsePeerIdWithoutRepeatedTurnover()
    {
        ElectionClock clock = new();
        PeerMembership first = Create("a", clock);
        PeerMembership second = Create("b", clock);
        first.SetLocal(EPeerStatus.Ready);
        second.SetLocal(EPeerStatus.Ready);
        for (int index = 0; index < 10; index++)
        {
            Exchange(second, first);
            Assert.Equal("a", first.Coordinator);
            Assert.Equal("a", second.Coordinator);
        }
    }

    /// <summary>同一条件の独立Peerを用意します。</summary>
    /// <returns>試験対象の参加管理です。</returns>
    private static PeerMembership Create(string peer, TimeProvider clock) => new(new("cluster", peer, peer, "127.0.0.1", "127.255.255.255", 12345, 1, true, Path.GetTempPath()) { PeerTimeout = TimeSpan.FromSeconds(2) }, Guid.NewGuid(), clock);

    /// <summary>過去の通信断によるローカル履歴を、実際のExpire経路で作ります。</summary>
    private static void RememberExpiredPeer(PeerMembership membership, ElectionClock clock)
    {
        PeerInfo ghost = new("departed", Guid.NewGuid(), "departed", "127.0.0.1", 12345, EPeerStatus.Joining, 1, null, clock.GetUtcNow());
        Assert.True(membership.Observe(new(ghost, "same", null, null, 1), ghost.Address));
        clock.Advance(TimeSpan.FromSeconds(3));
        membership.Expire();
    }

    /// <summary>受信前の二つのハートビートを固定し、配送順に依存しない結果を検証します。</summary>
    private static void Exchange(PeerMembership first, PeerMembership second)
    {
        PeerAnnouncement a = Announcement(first);
        PeerAnnouncement b = Announcement(second);
        Assert.True(first.Observe(b, b.Peer.Address));
        Assert.True(second.Observe(a, a.Peer.Address));
    }

    /// <summary>本体と同じローカル実体・担当・前担当を送信します。</summary>
    /// <returns>送信時点の不変通知です。</returns>
    private static PeerAnnouncement Announcement(PeerMembership membership) => membership.CreateAnnouncement("same");

    /// <summary>長い実時間待機なしに無通信期限と参加順を分離します。</summary>
    private sealed class ElectionClock(DateTimeOffset? initial = null) : TimeProvider
    {
        private DateTimeOffset now = initial ?? DateTimeOffset.UtcNow;

        /// <summary>現在の試験時刻です。</summary>
        /// <returns>単調に進めるUTC時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => now;

        /// <summary>無通信時間だけを進めます。</summary>
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
