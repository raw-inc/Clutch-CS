using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>更新操作を持たない観測の順序、鮮度、保持上限を検証します。</summary>
public sealed class FleetUpdateObservationTests
{
    /// <summary>同じ通知の再読込みで鮮度を延命せず、新しい所有者通知だけで復旧します。</summary>
    [Fact]
    public void RepeatedReadsDoNotTurnAStoppedPublisherIntoLiveProgress()
    {
        LogTestClock clock = new();
        FleetUpdateObservationStore store = new(clock);
        FleetUpdateObservation first = Observation();
        Assert.True(store.Observe(first));
        clock.Advance(FleetUpdateObservationStore.Freshness);
        Assert.False(store.Observe(first));
        FleetUpdateObservation stale = Assert.Single(store.Read());
        Assert.True(stale.Stale);
        Assert.False(stale.Activity.Completed);
        Assert.Equal(EOutcome.Unknown, stale.Activity.Outcome);
        Assert.True(store.Observe(first with { Sequence = 2 }));
        Assert.False(Assert.Single(store.Read()).Stale);
        Assert.False(store.Observe(first));
    }

    /// <summary>古いCoreに残った通知で完了結果を巻き戻しません。</summary>
    [Fact]
    public void CompletionAndPublisherRestartUseMonotonicProgress()
    {
        FleetUpdateObservationStore store = new();
        FleetUpdateObservation first = Observation();
        store.Observe(first);
        FleetUpdateObservation finished = first with { Sequence = 2, Activity = first.Activity with { Completed = true, Confirmed = 2, Outcome = EOutcome.Succeeded, UpdatedAt = first.Activity.UpdatedAt.AddSeconds(1) } };
        Assert.True(store.Observe(finished));
        Assert.False(store.Observe(first));
        Assert.False(store.Observe(first with { PublisherId = Guid.NewGuid(), Sequence = 99 }));
        Assert.True(store.Observe(finished with { PublisherId = Guid.NewGuid(), Sequence = 1, Activity = finished.Activity with { UpdatedAt = finished.Activity.UpdatedAt.AddSeconds(1) } }));
        Assert.True(Assert.Single(store.Read()).Activity.Completed);
    }

    /// <summary>転送された鮮度切れを、新しく受信したという理由で解除しません。</summary>
    [Fact]
    public void ForwardedStaleObservationRemainsUnconfirmed()
    {
        FleetUpdateObservationStore store = new();
        store.Observe(Observation() with { Stale = true });
        Assert.True(Assert.Single(store.Read()).Stale);
    }

    /// <summary>完了・未完了とも有限保持し、通知数でメモリが増え続けません。</summary>
    [Fact]
    public void RetentionAndOperationCountAreBounded()
    {
        LogTestClock clock = new();
        FleetUpdateObservationStore store = new(clock);
        FleetUpdateObservation first = Observation();
        store.Observe(first);
        store.Observe(Observation() with { Activity = first.Activity with { OperationId = Guid.NewGuid(), Completed = true } });
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(Assert.Single(store.Read()).Activity.Completed);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(store.Read());
        for (int index = 0; index < 40; index++)
        {
            store.Observe(Observation());
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(32, store.Read().Count);
    }

    /// <summary>不正な要約や過大な対象一覧を受理しません。</summary>
    [Fact]
    public void RejectsInvalidCountersAndMissingTargetNames()
    {
        FleetUpdateObservationStore store = new();
        FleetUpdateObservation first = Observation();
        Assert.Throws<ArgumentException>(() => store.Observe(first with { Sequence = 0 }));
        Assert.Throws<ArgumentException>(() => store.Observe(first with { Activity = first.Activity with { Confirmed = 3 } }));
        Assert.ThrowsAny<ArgumentException>(() => store.Observe(first with { Activity = first.Activity with { Current = [new("cluster", "peer", null!, "Updating")] } }));
        Assert.Throws<ArgumentNullException>(() => store.Observe(first with { Activity = null! }));
        Assert.Empty(store.Read());
    }

    /// <summary>入力配列の後続変更を保持中の表示へ持ち込みません。</summary>
    [Fact]
    public void CapturesThePublishedTargetArray()
    {
        FleetUpdateObservationStore store = new();
        FleetUpdateActiveTarget[] targets = [new("cluster", "peer", "host", "Updating")];
        FleetUpdateObservation first = Observation();
        store.Observe(first with { Activity = first.Activity with { Current = targets } });
        targets[0] = targets[0] with { State = "Completed" };
        Assert.Equal("Updating", Assert.Single(Assert.Single(store.Read()).Activity.Current).State);
    }

    /// <summary>配布元のパスや更新要求を含まない要約です。</summary>
    /// <returns>表示だけの試験通知です。</returns>
    private static FleetUpdateObservation Observation() => new(Guid.NewGuid(), 1,
        new(Guid.NewGuid(), "test-release", false, EOutcome.Unknown, 1, 2, 0, DateTimeOffset.UtcNow, []));
}
