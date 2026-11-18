using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>日時補正・通知の再送と、実際に通信が途絶えた時間を区別します。</summary>
public sealed class PeerLivenessClockTests
{
    /// <summary>時計補正は生存期間を変えず、期限直後にだけUnavailableへ進みます。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void ClockCorrectionAndDuplicateAnnouncementsDoNotChangeLiveness(int seconds)
    {
        AdmissionTestClock clock = new();
        PeerMembership membership = Membership(clock);
        PeerAnnouncement first = Announcement();
        Assert.True(membership.Observe(first, first.Peer.Address));
        DateTimeOffset received = Remote(membership).LastSeen;
        clock.Adjust(TimeSpan.FromSeconds(seconds));
        membership.Expire();
        Assert.Equal(EPeerStatus.Ready, Remote(membership).Status);
        Assert.Equal(received, Remote(membership).LastSeen);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(membership.Observe(first, first.Peer.Address));
        membership.Expire();
        Assert.Equal(EPeerStatus.Ready, Remote(membership).Status);
        clock.Advance(TimeSpan.FromTicks(1));
        membership.Expire();
        Assert.Equal(EPeerStatus.Unavailable, Remote(membership).Status);
        Assert.False(membership.Observe(first, first.Peer.Address));
        Assert.Equal(EPeerStatus.Unavailable, Remote(membership).Status);
        Assert.True(membership.Observe(first with { Sequence = 2 }, first.Peer.Address));
        Assert.Equal(EPeerStatus.Ready, Remote(membership).Status);
        Assert.Equal(clock.GetUtcNow(), Remote(membership).LastSeen);
    }

    /// <summary>有効な同実体の通信は猶予を更新しますが、参加状態をReadyへ昇格させません。</summary>
    [Fact]
    public void TrafficRefreshRequiresTheKnownInstanceAndDoesNotPromoteIt()
    {
        AdmissionTestClock clock = new();
        PeerMembership membership = Membership(clock);
        PeerAnnouncement first = Announcement();
        Assert.True(membership.Observe(first, first.Peer.Address));
        clock.Advance(TimeSpan.FromSeconds(9));
        membership.ObserveTraffic(first.Peer.PeerId, first.Peer.InstanceId);
        clock.Advance(TimeSpan.FromSeconds(9));
        membership.Expire();
        Assert.Equal(EPeerStatus.Ready, Remote(membership).Status);
        membership.ObserveTraffic(first.Peer.PeerId, Guid.NewGuid());
        clock.Advance(TimeSpan.FromSeconds(2));
        membership.Expire();
        Assert.Equal(EPeerStatus.Unavailable, Remote(membership).Status);
        membership.ObserveTraffic(first.Peer.PeerId, first.Peer.InstanceId);
        Assert.Equal(TimeSpan.Zero, Remote(membership).AgeAt(clock.GetUtcNow()));
        Assert.Equal(EPeerStatus.Unavailable, Remote(membership).Status);
        Assert.True(membership.Observe(first with { Sequence = 2, Peer = first.Peer with { Status = EPeerStatus.Detached } }, first.Peer.Address));
        membership.ObserveTraffic(first.Peer.PeerId, first.Peer.InstanceId);
        Assert.Equal(EPeerStatus.Detached, Remote(membership).Status);
    }

    /// <summary>片方向の途絶は受信側だけで失効し、全体の時計やCoordinatorを必要としません。</summary>
    [Fact]
    public void OneWayLossExpiresOnlyTheReceiverWithoutTraffic()
    {
        AdmissionTestClock firstClock = new(), secondClock = new();
        secondClock.Adjust(TimeSpan.FromHours(-2));
        PeerMembership first = Membership(firstClock), second = Membership(secondClock, "remote");
        first.SetLocal(EPeerStatus.Ready);
        second.SetLocal(EPeerStatus.Ready);
        Assert.True(first.Observe(second.CreateAnnouncement("same"), "127.0.0.1"));
        Assert.True(second.Observe(first.CreateAnnouncement("same"), "127.0.0.1"));
        firstClock.Advance(TimeSpan.FromSeconds(11));
        secondClock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(first.Observe(second.CreateAnnouncement("same"), "127.0.0.1"));
        first.Expire();
        second.Expire();
        Assert.Equal(EPeerStatus.Ready, Remote(first).Status);
        Assert.Equal(EPeerStatus.Unavailable, Assert.Single(second.Snapshot(), peer => string.Equals(peer.PeerId, "local", StringComparison.Ordinal)).Status);
        PeerMembership restarted = Membership(firstClock);
        restarted.RestoreKnown(first.ExportKnown().Peers);
        Assert.Equal(EPeerStatus.Unavailable, Remote(restarted).Status);
        Assert.Null(Remote(restarted).ObservationAge);
    }

    /// <summary>Observerも経過時間で失効し、期限切れ通知の再送では復活しません。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void ObserverExpiryUsesElapsedTimeAndRetainsAcceptedSequence(int seconds)
    {
        AdmissionTestClock clock = new();
        ObserverCatalog catalog = new(clock, TimeSpan.FromSeconds(10));
        PeerAnnouncement first = Announcement();
        Observe(catalog, first);
        clock.Adjust(TimeSpan.FromSeconds(seconds));
        Assert.Single(catalog.Snapshot());
        clock.Advance(TimeSpan.FromSeconds(9));
        Observe(catalog, first);
        Assert.Equal(TimeSpan.FromSeconds(9), Assert.Single(Assert.Single(catalog.Snapshot()).Peers).AgeAt(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(catalog.Snapshot());
        Observe(catalog, first);
        Assert.Empty(catalog.Snapshot());
        Observe(catalog, first with { Sequence = 2 });
        Assert.Single(catalog.Snapshot());
    }

    /// <summary>UTCを戻しても、容量上限に達したObserverは本当に期限を過ぎた観測を回収します。</summary>
    [Fact]
    public void ObserverCapacityReclaimsElapsedObservationsAfterClockRollback()
    {
        AdmissionTestClock clock = new();
        ObserverCatalog catalog = new(clock, TimeSpan.FromSeconds(10));
        for (int index = 0; index < 1024; index++)
        {
            PeerAnnouncement announcement = Announcement();
            Observe(catalog, announcement with { Peer = announcement.Peer with { PeerId = index.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
        }
        clock.Adjust(TimeSpan.FromHours(-1));
        clock.Advance(TimeSpan.FromSeconds(10));
        Observe(catalog, Announcement());
        Assert.Single(Assert.Single(catalog.Snapshot()).Peers);
    }

    /// <summary>相手の日時が未来でも、受信した通信から生存を判定する設定です。</summary>
    /// <returns>10秒間の無通信を許容するmembershipです。</returns>
    private static PeerMembership Membership(TimeProvider clock, string peer = "local") => new(new("cluster", peer, peer, "127.0.0.1", "127.255.255.255", 12345, 1, true, Path.GetTempPath()) { PeerTimeout = TimeSpan.FromSeconds(10) }, Guid.NewGuid(), clock);

    /// <summary>送信元の壁時計は受信側とは一致させません。</summary>
    /// <returns>一つの実体の最初の通知です。</returns>
    private static PeerAnnouncement Announcement() => new(new("remote", Guid.NewGuid(), "remote", "127.0.0.1", 12345, EPeerStatus.Ready, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue), "same", null, null, 1);

    /// <summary>受信側が保持する相手の状態です。</summary>
    /// <returns>現在の参加情報です。</returns>
    private static PeerInfo Remote(PeerMembership membership) => Assert.Single(membership.Snapshot(), peer => string.Equals(peer.PeerId, "remote", StringComparison.Ordinal));

    /// <summary>実通知と同じ識別子・本文をObserverへ渡します。</summary>
    private static void Observe(ObserverCatalog catalog, PeerAnnouncement announcement)
    {
        WireMessage message = new(Guid.NewGuid(), "cluster", announcement.Peer.PeerId, announcement.Peer.InstanceId, "hello", null, null, JsonSerializer.SerializeToElement(announcement));
        catalog.Observe(message, announcement, announcement.Peer.Address);
    }
}
