using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>送信元実体・通知順序・経過時間で参加表示の寿命を確認します。</summary>
public sealed class PresenceLifetimeTests
{
    /// <summary>日時の補正や同じ通知では延長せず、期間を過ぎた通知も再表示しません。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void DuplicateAndExpiredAnnouncementsCannotRenewPresence(int correctionSeconds)
    {
        CollaborationClock clock = new();
        WorkflowFixture.TestPeers peers = new();
        PresenceTracker tracker = new(peers, clock, new());
        PresenceAnnouncement notice = tracker.Create(Update());
        tracker.Receive(peers.LocalPeerId, peers.LocalInstanceId, notice);
        clock.Advance(TimeSpan.FromSeconds(20));
        clock.Adjust(TimeSpan.FromSeconds(correctionSeconds));
        tracker.Receive(peers.LocalPeerId, peers.LocalInstanceId, notice);
        PresenceView current = Assert.Single(tracker.Read("workflow"));
        Assert.Equal(TimeSpan.FromSeconds(25), current.Remaining);
        clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Empty(tracker.Read("workflow"));
        tracker.Receive(peers.LocalPeerId, peers.LocalInstanceId, notice);
        tracker.ReceiveLegacy(peers.LocalPeerId, peers.LocalInstanceId, notice.Presence);
        Assert.Empty(tracker.Read("workflow"));
        tracker.Receive(peers.LocalPeerId, peers.LocalInstanceId, tracker.Create(notice.Presence));
        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(tracker.Read("workflow")).Remaining);
    }

    /// <summary>旧通知で新しい座標を上書きせず、受信元実体が変われば新しい連番として扱います。</summary>
    [Fact]
    public void RestartedOriginCannotBeReplacedByItsPreviousInstance()
    {
        CollaborationClock clock = new();
        WorkflowFixture.TestPeers peers = new();
        Guid old = Guid.NewGuid(), current = Guid.NewGuid();
        PeerInfo source = new("remote", old, "host", "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, clock.GetUtcNow());
        peers.Others = [source];
        PresenceTracker tracker = new(peers, clock, new());
        PresenceUpdate update = Update();
        tracker.Receive(source.PeerId, old, new(update with { CursorX = 20 }, 20, TimeSpan.FromSeconds(45).Ticks));
        tracker.Receive(source.PeerId, old, new(update with { CursorX = 10 }, 19, TimeSpan.FromSeconds(45).Ticks));
        Assert.Equal(20, Assert.Single(tracker.Read("workflow")).Presence.CursorX);
        peers.Others = [source with { InstanceId = current }];
        tracker.Receive(source.PeerId, old, new(update, 21, TimeSpan.FromSeconds(45).Ticks));
        Assert.Empty(tracker.Read("workflow"));
        tracker.Receive(source.PeerId, current, new(update, 1, TimeSpan.FromSeconds(45).Ticks));
        Assert.Equal(current, Assert.Single(tracker.Read("workflow")).PeerInstanceId);
    }

    /// <summary>保持上限で外した通知を再送しても、別のUIの現在表示を追い出しません。</summary>
    [Fact]
    public void EvictedAnnouncementsStayRetiredForTheirOriginInstance()
    {
        CollaborationClock clock = new();
        WorkflowFixture.TestPeers peers = new();
        PresenceTracker tracker = new(peers, clock, new() { MaximumPresence = 1 });
        PresenceAnnouncement first = tracker.Create(Update());
        tracker.Receive(peers.LocalPeerId, peers.LocalInstanceId, first);
        clock.Advance(TimeSpan.FromSeconds(1));
        PresenceAnnouncement second = tracker.Create(Update());
        tracker.Receive(peers.LocalPeerId, peers.LocalInstanceId, second);
        tracker.Receive(peers.LocalPeerId, peers.LocalInstanceId, first);
        Assert.Equal(second.Presence.UiInstanceId, Assert.Single(tracker.Read("workflow")).Presence.UiInstanceId);
    }

    /// <summary>旧Coreの表示は受信側で計測し、新形式への移行後は互換通知で延長しません。</summary>
    [Fact]
    public void CompatibilityNoticeCannotExtendSequencedPresence()
    {
        CollaborationClock clock = new();
        WorkflowFixture.TestPeers peers = new();
        PresenceTracker tracker = new(peers, clock, new());
        PresenceUpdate update = Update();
        tracker.ReceiveLegacy(peers.LocalPeerId, peers.LocalInstanceId, update);
        Assert.Null(Assert.Single(tracker.Read("workflow")).Sequence);
        PresenceAnnouncement notice = tracker.Create(update);
        tracker.Receive(peers.LocalPeerId, peers.LocalInstanceId, notice);
        clock.Advance(TimeSpan.FromSeconds(40));
        tracker.ReceiveLegacy(peers.LocalPeerId, peers.LocalInstanceId, update);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(tracker.Read("workflow")).Remaining);
    }

    /// <summary>未保存変更を持たない一画面の表示を作ります。</summary>
    /// <returns>新しいUI実体の通知本文です。</returns>
    private static PresenceUpdate Update() => new("workflow", Guid.NewGuid(), "host", "Editor", []);
}
