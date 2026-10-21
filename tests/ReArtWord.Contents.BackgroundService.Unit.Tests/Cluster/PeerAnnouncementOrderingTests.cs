using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>同実体の逆順通知が離脱・担当・生存観測を巻き戻さないことを確認します。</summary>
public sealed class PeerAnnouncementOrderingTests
{
    /// <summary>通常通信が続く同じ実体は失効させず、旧実体の通信や状態昇格には使いません。</summary>
    [Fact]
    public void VerifiedTrafficKeepsLivenessWithoutPromotingParticipation()
    {
        AnnouncementClock clock = new();
        PeerMembership membership = Membership(clock);
        membership.SetLocal(EPeerStatus.Ready);
        PeerAnnouncement ready = Announcement(EPeerStatus.Ready, 1);
        Assert.True(membership.Observe(ready, "127.0.0.1"));
        for (int index = 0; index < 12; index++)
        {
            clock.Advance();
            membership.ObserveTraffic(ready.Peer.PeerId, ready.Peer.InstanceId);
            membership.Expire();
            Assert.Equal(EPeerStatus.Ready, Remote(membership).Status);
            Assert.Equal("a", membership.Coordinator);
        }
        for (int index = 0; index < 6; index++)
        {
            clock.Advance();
            membership.ObserveTraffic(ready.Peer.PeerId, Guid.NewGuid());
        }
        membership.Expire();
        Assert.Equal(EPeerStatus.Unavailable, Remote(membership).Status);
        membership.ObserveTraffic(ready.Peer.PeerId, ready.Peer.InstanceId);
        Assert.Equal(EPeerStatus.Unavailable, Remote(membership).Status);
        Assert.Equal("b", membership.Coordinator);
    }

    /// <summary>遅着した背景同期は、同時に確定した内容競合を同期待ちへ巻き戻しません。</summary>
    [Fact]
    public void BackgroundSynchronizationCannotOverwriteAnImportConflict()
    {
        PeerMembership membership = Membership(new AnnouncementClock());
        membership.SetLocal(EPeerStatus.Ready);
        membership.SetLocal(EPeerStatus.Syncing, preserveConflict: true);
        Assert.Equal(EPeerStatus.Syncing, Assert.Single(membership.Snapshot()).Status);
        membership.SetLocal(EPeerStatus.Conflict);
        membership.SetLocal(EPeerStatus.Syncing, preserveConflict: true);
        Assert.Equal(EPeerStatus.Conflict, Assert.Single(membership.Snapshot()).Status);
        membership.SetLocal(EPeerStatus.Syncing);
        Assert.Equal(EPeerStatus.Syncing, Assert.Single(membership.Snapshot()).Status);
    }

    /// <summary>新Detachedを古いReadyで上書きせず、番号が進んだ正規復帰だけを受け入れます。</summary>
    [Fact]
    public void OldReadyCannotUndoObservedDetach()
    {
        AnnouncementClock clock = new();
        PeerMembership membership = Membership(clock);
        membership.SetLocal(EPeerStatus.Ready);
        PeerAnnouncement ready = Announcement(EPeerStatus.Ready, 1);
        Assert.True(membership.Observe(ready, "127.0.0.1"));
        Assert.True(membership.IsCurrent(ready));
        Assert.Equal("a", membership.Coordinator);
        clock.Advance();
        PeerAnnouncement detached = ready with { Sequence = 3, Peer = ready.Peer with { Status = EPeerStatus.Detached, ReadySince = null }, Coordinator = "b" };
        Assert.True(membership.Observe(detached, "127.0.0.1"));
        DateTimeOffset observed = clock.GetUtcNow();
        clock.Advance();
        Assert.False(membership.Observe(ready with { Sequence = 2, Peer = ready.Peer with { LastSeen = DateTimeOffset.MaxValue } }, "127.0.0.1"));
        Assert.False(membership.Observe(detached, "127.0.0.1"));
        PeerInfo current = Remote(membership);
        Assert.Equal(EPeerStatus.Detached, current.Status);
        Assert.Equal(observed, current.LastSeen);
        Assert.False(membership.IsCurrent(ready));
        Assert.Equal("b", membership.Coordinator);
        Assert.True(membership.Observe(ready with { Sequence = 4, Peer = ready.Peer with { Status = EPeerStatus.Syncing } }, "127.0.0.1"));
        Assert.True(membership.Observe(ready with { Sequence = 5 }, "127.0.0.1"));
        Assert.Equal(EPeerStatus.Ready, Remote(membership).Status);
    }

    /// <summary>Observerにも同じ新旧判定を適用し、重複でlocal受信時刻を延長しません。</summary>
    [Fact]
    public void ObserverRejectsOlderAndDuplicateAnnouncements()
    {
        AnnouncementClock clock = new();
        ObserverCatalog catalog = new(clock, TimeSpan.FromSeconds(10));
        PeerAnnouncement ready = Announcement(EPeerStatus.Ready, 1);
        Observe(catalog, ready);
        clock.Advance();
        PeerAnnouncement detached = ready with { Sequence = 3, Peer = ready.Peer with { Status = EPeerStatus.Detached }, Coordinator = "b" };
        Observe(catalog, detached);
        DateTimeOffset observed = clock.GetUtcNow();
        clock.Advance();
        Observe(catalog, ready with { Sequence = 2 });
        Observe(catalog, detached);
        DiscoveredCluster cluster = Assert.Single(catalog.Snapshot());
        Assert.Equal(EPeerStatus.Detached, Assert.Single(cluster.Peers).Status);
        Assert.Equal(observed, Assert.Single(cluster.Peers).LastSeen);
        Assert.Equal(observed, cluster.LastSeen);
        Assert.Equal("b", Assert.Single(cluster.CoordinatorCandidates));
        for (int index = 0; index < 10; index++)
        {
            clock.Advance();
        }
        Assert.Empty(catalog.Snapshot());
        Observe(catalog, ready with { Sequence = 2 });
        Assert.Empty(catalog.Snapshot());
        Observe(catalog, ready with { Sequence = 4 });
        Assert.Equal(EPeerStatus.Ready, Assert.Single(Assert.Single(catalog.Snapshot()).Peers).Status);
    }

    /// <summary>未指定0と負数は新しい実体でも受理せず、正の番号だけを使います。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnorderedAnnouncementsAreNotMembershipEvidence(long sequence)
    {
        AnnouncementClock clock = new();
        PeerMembership membership = Membership(clock);
        ObserverCatalog catalog = new(clock, TimeSpan.FromSeconds(10));
        PeerAnnouncement invalid = Announcement(EPeerStatus.Ready, sequence);
        Assert.False(membership.Observe(invalid, "127.0.0.1"));
        Observe(catalog, invalid);
        Assert.Single(membership.Snapshot());
        Assert.Empty(catalog.Snapshot());
    }

    /// <summary>新しい実体では番号を再開でき、送信時の状態と番号を一緒に固定します。</summary>
    [Fact]
    public void SenderCapturesStateAndSequenceWithinOneInstance()
    {
        AnnouncementClock clock = new();
        PeerMembership sender = Membership(clock);
        sender.SetLocal(EPeerStatus.Ready);
        PeerAnnouncement first = sender.CreateAnnouncement("one");
        sender.SetLocal(EPeerStatus.Detached);
        PeerAnnouncement second = sender.CreateAnnouncement("two");
        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(EPeerStatus.Ready, first.Peer.Status);
        Assert.Equal(EPeerStatus.Detached, second.Peer.Status);
        Assert.Equal(clock.GetUtcNow(), second.Peer.LastSeen);
        PeerMembership receiver = Membership(clock);
        PeerAnnouncement prior = Announcement(EPeerStatus.Detached, 100);
        Assert.True(receiver.Observe(prior, "127.0.0.1"));
        PeerAnnouncement restarted = prior with { Sequence = 1, Peer = prior.Peer with { InstanceId = Guid.NewGuid(), Status = EPeerStatus.Ready } };
        Assert.True(receiver.Observe(restarted, "127.0.0.1"));
        Assert.Equal(restarted.Peer.InstanceId, Remote(receiver).InstanceId);
    }

    /// <summary>受信側のローカルメンバーを作ります。</summary>
    /// <returns>永続I/Oを使わない実membershipです。</returns>
    private static PeerMembership Membership(TimeProvider clock) => new(new("cluster", "b", "b", "127.0.0.1", "127.255.255.255", 12345, 1, true, Path.GetTempPath()), Guid.NewGuid(), clock);

    /// <summary>送信時刻が未来でも連番だけで新旧を判定する通知です。</summary>
    /// <returns>受信試験用の同実体通知です。</returns>
    private static PeerAnnouncement Announcement(EPeerStatus status, long sequence) => new(new("a", Guid.NewGuid(), "a", "127.0.0.1", 12345, status, 2, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue), "digest", "a", null, sequence);

    /// <summary>ローカル受信時刻に置き換えられた相手だけを返します。</summary>
    /// <returns>保存した実観測です。</returns>
    private static PeerInfo Remote(PeerMembership membership) => membership.Snapshot().Single(peer => string.Equals(peer.PeerId, "a", StringComparison.Ordinal));

    /// <summary>実serializerを通した通知をObserverへ渡します。</summary>
    private static void Observe(ObserverCatalog catalog, PeerAnnouncement announcement)
    {
        JsonElement body = JsonSerializer.SerializeToElement(announcement);
        WireMessage message = new(Guid.NewGuid(), "cluster", "a", announcement.Peer.InstanceId, "hello", null, null, body);
        catalog.Observe(message, body.Deserialize<PeerAnnouncement>()!, "127.0.0.1");
    }

    /// <summary>送信時計と無関係の受信時計です。</summary>
    private sealed class AnnouncementClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch.AddYears(50);

        /// <summary>ローカル受信時刻を進めます。</summary>
        internal void Advance() => now = now.AddSeconds(1);

        /// <summary>受信側だけの時計を返します。</summary>
        /// <returns>現在のローカル時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => now;
    }
}
