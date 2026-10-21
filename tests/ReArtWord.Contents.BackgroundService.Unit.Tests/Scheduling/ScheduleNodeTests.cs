using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scheduling;

/// <summary>時間指定の未実行回集約と正常終了を検証します。</summary>
public sealed class ScheduleNodeTests
{
    /// <summary>未実行intervalが累積しても最新回だけを通知します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task DelayedIntervalsEmitLatestOccurrenceOnly()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-13T12:00:05Z", CultureInfo.InvariantCulture);
        NodeExecutionContext item = NodeTestContext.Create("""{"mode":"interval","start":"2026-09-13T12:00:00Z","intervalMilliseconds":1000,"count":1}""", time: new FixedClock(now));
        List<NodeEmission> emissions = [];
        NodeSourceContext source = new(item, () => item, (emission, _) =>
        {
            emissions.Add(emission);
            return ValueTask.CompletedTask;
        }, () => false);
        NodeResult terminal = await new ScheduleNode().RunAsync(source);
        Assert.Equal(EOutcome.Succeeded, terminal.Outcome);
        Assert.Single(emissions);
        Assert.Equal(now, emissions[0].Result.Outputs["scheduledAt"].Json.GetDateTimeOffset());
    }

    /// <summary>除外した一回指定は発火せず正常終了します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ExcludedOnceDoesNotExecute()
    {
        NodeExecutionContext item = NodeTestContext.Create("""{"mode":"once","at":"2026-09-13T12:00:00Z","excludeDates":["2026-09-13"]}""");
        List<NodeEmission> emissions = [];
        NodeSourceContext source = new(item, () => item, (emission, _) =>
        {
            emissions.Add(emission);
            return ValueTask.CompletedTask;
        }, () => false);
        Assert.Equal(EOutcome.Succeeded, (await new ScheduleNode().RunAsync(source)).Outcome);
        Assert.Empty(emissions);
    }

    /// <summary>予定停止トークンで未来時刻の待機を正常に終了できます。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task PlannedStopIsNotCancellationFailure()
    {
        NodeExecutionContext item = NodeTestContext.Create("""{"mode":"interval","intervalMilliseconds":60000}""");
        using CancellationTokenSource stop = new();
        await stop.CancelAsync();
        NodeSourceContext source = new(item, () => item, (_, _) => ValueTask.CompletedTask, () => false, stopToken: stop.Token);
        Assert.Equal(EOutcome.Succeeded, (await new ScheduleNode().RunAsync(source)).Outcome);
    }

    /// <summary>曜日外の明示追加日を実行回として利用します。</summary>
    /// <returns>例外日の検証の完了。</returns>
    [Fact]
    public async Task IncludedDateOverridesWeekdaySelection()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-13T12:00:00Z", CultureInfo.InvariantCulture);
        NodeExecutionContext item = NodeTestContext.Create("""{"mode":"weekdays","weekdays":[1],"time":"12:00","start":"2026-09-11T00:00:00Z","end":"2026-09-13T23:59:59Z","includeDates":["2026-09-13"],"count":1}""", time: new FixedClock(now));
        List<NodeEmission> emissions = [];
        NodeSourceContext source = new(item, () => item, (emission, _) =>
        {
            emissions.Add(emission);
            return ValueTask.CompletedTask;
        }, () => false);
        Assert.Equal(EOutcome.Succeeded, (await new ScheduleNode().RunAsync(source)).Outcome);
        Assert.Single(emissions);
        Assert.Equal(now, emissions[0].Result.Outputs["scheduledAt"].Json.GetDateTimeOffset());
    }

    /// <summary>時刻品質未取得は測定済みfalseにせず警告して継続します。</summary>
    /// <returns>警告と実行の検証の完了。</returns>
    [Fact]
    public async Task UnspecifiedClockQualityWarnsAndContinues()
    {
        NodeExecutionContext item = NodeTestContext.Create("""{"mode":"once","at":"2020-01-01T00:00:00Z"}""");
        List<NodeSourceStatus> statuses = [];
        List<NodeEmission> emissions = [];
        NodeSourceContext source = new(item, () => item, (emission, _) =>
        {
            emissions.Add(emission);
            return ValueTask.CompletedTask;
        }, () => false, (status, _) =>
        {
            statuses.Add(status);
            return ValueTask.CompletedTask;
        });
        Assert.Equal(EOutcome.Succeeded, (await new ScheduleNode().RunAsync(source)).Outcome);
        Assert.Single(emissions);
        Assert.Contains(statuses, status => string.Equals(status.ReasonCode, "schedule.clock.unknown", StringComparison.Ordinal));
    }

}

/// <summary>試験が待機せず経過済み回を選べる時計です。</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>固定した観測時刻を取得します。</summary>
    /// <returns>試験の現在時刻です。</returns>
    public override DateTimeOffset GetUtcNow()
    {
        return now;
    }
}
