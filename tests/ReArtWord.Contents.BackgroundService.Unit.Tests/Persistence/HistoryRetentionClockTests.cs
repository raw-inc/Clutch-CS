using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>履歴整理の基準が、UTC補正・参照の循環・元Peerの離脱で先へ飛ばないことを確認します。</summary>
public sealed class HistoryRetentionClockTests
{
    /// <summary>UTCを一か月進めても一か月分を経過扱いせず、戻した時計より先にも整理を進めません。</summary>
    [Fact]
    public void ForwardAndBackwardCorrectionsCannotAdvanceRetirement()
    {
        ResourceSelectionClock source = new();
        HistoryRetentionClock clock = new(source);
        DateTimeOffset initial = clock.Capture();
        source.Adjust(TimeSpan.FromDays(31));
        source.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(initial.AddMinutes(1), clock.Capture());
        source.Adjust(TimeSpan.FromDays(-62));
        Assert.Equal(source.GetUtcNow(), clock.Capture());
        source.Advance(TimeSpan.FromDays(1));
        Assert.Equal(source.GetUtcNow(), clock.Capture());
    }

    /// <summary>同じ起点が他Peerから戻ってきても、通信のたびに基準日時を遅らせ続けません。</summary>
    [Fact]
    public void RelayedReferenceDoesNotAccumulateRoundTripDelay()
    {
        ResourceSelectionClock originTime = new(), firstTime = new(), secondTime = new();
        originTime.Adjust(TimeSpan.FromDays(-31));
        HistoryRetentionClock origin = new(originTime), first = new(firstTime), second = new(secondTime);
        HistoryTimeReference reference = origin.Sample();
        first.Observe(reference);
        second.Observe(first.Sample());
        DateTimeOffset before = first.Capture();
        for (int index = 0; index < 100; index++)
        {
            HistoryTimeReference fromFirst = first.Sample();
            firstTime.Advance(TimeSpan.FromSeconds(1));
            secondTime.Advance(TimeSpan.FromSeconds(1));
            second.Observe(fromFirst);
            HistoryTimeReference fromSecond = second.Sample();
            firstTime.Advance(TimeSpan.FromSeconds(1));
            secondTime.Advance(TimeSpan.FromSeconds(1));
            first.Observe(fromSecond);
        }
        Assert.Equal(before.AddSeconds(200), first.Capture());
        Assert.Equal(first.Capture(), second.Capture());
        Assert.Equal(reference.OriginId, first.Sample().OriginId);
    }

    /// <summary>元Peerへの問い合わせがなくなっても計測を続け、後から参加したPeerにも同じ起点を渡せます。</summary>
    [Fact]
    public void OriginAbsenceAndLateJoinKeepTheExistingReference()
    {
        ResourceSelectionClock originTime = new(), receiverTime = new(), lateTime = new();
        originTime.Adjust(TimeSpan.FromDays(-31));
        HistoryRetentionClock origin = new(originTime), receiver = new(receiverTime), late = new(lateTime);
        HistoryTimeReference reference = origin.Sample();
        receiver.Observe(reference);
        receiverTime.Advance(TimeSpan.FromDays(1));
        lateTime.Advance(TimeSpan.FromDays(1));
        late.Observe(receiver.Sample());
        Assert.Equal(reference.Time.AddDays(1), late.Capture());
        Assert.Equal(reference.OriginId, late.Sample().OriginId);
        receiverTime.Advance(TimeSpan.FromMinutes(1));
        lateTime.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(receiver.Capture(), late.Capture());
    }

    /// <summary>同じ起点のUTCが戻った通知は反映し、既に測った経過時間は初期化しません。</summary>
    [Fact]
    public void EarlierEpochChangesTheReferenceWithoutResettingElapsedTime()
    {
        ResourceSelectionClock originTime = new(), receiverTime = new();
        originTime.Adjust(TimeSpan.FromHours(-1));
        HistoryRetentionClock origin = new(originTime), receiver = new(receiverTime);
        receiver.Observe(origin.Sample());
        originTime.Advance(TimeSpan.FromSeconds(30));
        receiverTime.Advance(TimeSpan.FromSeconds(30));
        originTime.Adjust(TimeSpan.FromDays(-31));
        receiver.Observe(origin.Sample());
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, receiver.Sample().ElapsedTicks);
        Assert.Equal(origin.Capture(), receiver.Capture());
    }

    /// <summary>中継通知では経過を戻さず、発行元の通知では先行した計測だけを補正します。</summary>
    [Fact]
    public void OnlyTheOriginCanCorrectAnAheadElapsedMeasurement()
    {
        ResourceSelectionClock source = new();
        HistoryRetentionClock clock = new(source);
        HistoryTimeReference reference = new(Guid.NewGuid(), source.GetUtcNow().AddDays(-31), 0);
        clock.Observe(reference);
        source.Advance(TimeSpan.FromSeconds(10));
        clock.Observe(reference with { ElapsedTicks = TimeSpan.FromSeconds(8).Ticks });
        Assert.Equal(reference.Time.AddSeconds(10), clock.Capture());
        clock.Observe(reference with { ElapsedTicks = TimeSpan.FromSeconds(8).Ticks }, fromOrigin: true);
        Assert.Equal(reference.Time.AddSeconds(8), clock.Capture());
        source.Advance(TimeSpan.FromSeconds(10));
        clock.Observe(reference with { ElapsedTicks = TimeSpan.FromSeconds(9).Ticks });
        Assert.Equal(reference.Time.AddSeconds(18), clock.Capture());
    }

    /// <summary>不正な識別子や日時範囲を、整理の基準として採用しません。</summary>
    [Fact]
    public void InvalidReferencesLeaveTheCurrentClockUntouched()
    {
        ResourceSelectionClock source = new();
        HistoryRetentionClock clock = new(source);
        DateTimeOffset before = clock.Capture();
        foreach (HistoryTimeReference invalid in new[]
        {
            new HistoryTimeReference(Guid.Empty, before, 0),
            new HistoryTimeReference(Guid.NewGuid(), before, -1),
            new HistoryTimeReference(Guid.NewGuid(), DateTimeOffset.MaxValue, 1),
        })
        {
            Assert.Throws<InvalidDataException>(() => clock.Observe(invalid));
            Assert.Equal(before, clock.Capture());
        }
    }
}
