using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scheduling;

/// <summary>予定受付の確定競合だけを再照合し、既受付と結果不明の回を繰り返さないことを確認します。</summary>
public sealed class ScheduleConflictTests
{
    /// <summary>同期で進行位置が変わっても、最新の版で確認できた回だけを一度通知します。</summary>
    /// <returns>競合前後の読取り、通知数、状態回復の確認です。</returns>
    [Fact]
    public async Task RejectedReservationResumesFromConfirmedCursorAsync()
    {
        ScheduleCursor first = Initial();
        ScheduleCursor latest = first with { Version = "latest" };
        ScriptedCursor cursor = new([first, latest], [Rejected(first), latest with { Version = "accepted", Count = 1 }]);
        List<NodeEmission> emissions = [];
        List<NodeSourceStatus> statuses = [];
        NodeResult result = await RunAsync(cursor, emissions, statuses).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Single(emissions);
        Assert.Equal(["initial", "latest"], cursor.AdvancedVersions);
        int held = statuses.FindIndex(status => status.State == ESourceState.Unavailable);
        Assert.True(0 <= held);
        Assert.Contains(statuses.Skip(held + 1), status => status.State == ESourceState.Listening);
    }

    /// <summary>別担当が同じ一回を受付済みなら、古い回も完了通知も再発生させません。</summary>
    /// <returns>再読取り後に通知せず終了する確認です。</returns>
    [Fact]
    public async Task AlreadyAcceptedOccurrenceDoesNotRepeatAfterConflictAsync()
    {
        ScheduleCursor first = Initial();
        ScriptedCursor cursor = new([first, first with { Version = "other-owner", LastScheduledAt = DateTimeOffset.UnixEpoch, Count = 1 }], [Rejected(first)]);
        List<NodeEmission> emissions = [];
        NodeResult result = await RunAsync(cursor, emissions, []).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("alreadyCompleted", result.Route);
        Assert.Empty(emissions);
        Assert.Single(cursor.AdvancedVersions);
    }

    /// <summary>初期位置の作成競合も再照合し、既に終わった一回指定を通知しません。</summary>
    /// <returns>初期化競合からの安全な復旧確認です。</returns>
    [Fact]
    public async Task InitialCursorConflictRereadsAcceptedPositionAsync()
    {
        ScheduleCursor first = Initial();
        ScriptedCursor cursor = new([Rejected(first), first with { Version = "created", LastScheduledAt = DateTimeOffset.UnixEpoch, Count = 1 }], []);
        List<NodeEmission> emissions = [];
        NodeResult result = await RunAsync(cursor, emissions, []).ConfigureAwait(true);
        Assert.Equal("alreadyCompleted", result.Route);
        Assert.Equal(2, cursor.ReadCount);
        Assert.Empty(cursor.AdvancedVersions);
        Assert.Empty(emissions);
    }

    /// <summary>受付中止が確定していない失敗は再実行の対象にしません。</summary>
    /// <param name="outcome">最初の予約結果です。</param>
    /// <param name="reason">再照合を許可しない理由です。</param>
    /// <returns>一回の受付要求で終了する確認です。</returns>
    [Theory]
    [InlineData(EOutcome.Unknown, "Persistence.VersionConflict")]
    [InlineData(EOutcome.Unknown, "Replication.RequiredAcknowledgementMissing")]
    [InlineData(EOutcome.Failed, "Persistence.OperationMismatch")]
    [InlineData(EOutcome.NotExecuted, "schedule.ownerUnavailable")]
    public async Task UnconfirmedOrUnrelatedFailureIsNotRetriedAsync(EOutcome outcome, string reason)
    {
        ScheduleCursor first = Initial();
        ScriptedCursor cursor = new([first], [first with { Outcome = outcome, Reason = reason }]);
        List<NodeEmission> emissions = [];
        NodeResult result = await RunAsync(cursor, emissions, []).ConfigureAwait(true);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(1, cursor.ReadCount);
        Assert.Single(cursor.AdvancedVersions);
        Assert.Empty(emissions);
    }

    /// <summary>再読取りで結果不明が分かった場合も、新しい予約へ進みません。</summary>
    /// <returns>不確定な位置から実行しない確認です。</returns>
    [Fact]
    public async Task UnconfirmedRereadStopsWithoutAnotherReservationAsync()
    {
        ScheduleCursor first = Initial();
        ScriptedCursor cursor = new([first, first with { Outcome = EOutcome.Unknown, Reason = "schedule.cursorUnconfirmed" }], [Rejected(first)]);
        List<NodeEmission> emissions = [];
        NodeResult result = await RunAsync(cursor, emissions, []).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal(2, cursor.ReadCount);
        Assert.Single(cursor.AdvancedVersions);
        Assert.Empty(emissions);
    }

    /// <summary>競合による待機中でも、予定停止を失敗にせず速やかに終了します。</summary>
    /// <returns>未受付の回を通知しない停止確認です。</returns>
    [Fact]
    public async Task PlannedStopInterruptsConflictWaitAsync()
    {
        ScheduleCursor first = Initial();
        ScriptedCursor cursor = new([Rejected(first)], []);
        using CancellationTokenSource stop = new();
        NodeExecutionContext item = NodeTestContext.Create("""{"mode":"once","at":"1970-01-01T00:00:00Z"}""");
        List<NodeEmission> emissions = [];
        NodeSourceContext source = new(item, () => item, (emission, _) =>
        {
            emissions.Add(emission);
            return ValueTask.CompletedTask;
        }, () => false, async (status, _) =>
        {
            if (status.State == ESourceState.Unavailable)
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }, stop.Token) { Schedule = cursor };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        NodeResult result = await new ScheduleNode().RunAsync(source, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, cursor.ReadCount);
        Assert.Empty(emissions);
    }

    /// <summary>一回指定の進行がまだない確定位置です。</summary>
    /// <returns>比較更新に使用する初期位置です。</returns>
    private static ScheduleCursor Initial() => new("timeline", "initial", DateTimeOffset.UnixEpoch);

    /// <summary>全参加者で予約中止済みの版競合を返します。</summary>
    /// <returns>入力が受け付けられていない結果です。</returns>
    private static ScheduleCursor Rejected(ScheduleCursor cursor) => cursor with { Outcome = EOutcome.Failed, Reason = "Persistence.VersionConflict" };

    /// <summary>外部から確認できる通知と状態を保持し、有限の試験期限で一回指定を実行します。</summary>
    /// <returns>ノードの終了結果です。</returns>
    private static async Task<NodeResult> RunAsync(ScriptedCursor cursor, List<NodeEmission> emissions, List<NodeSourceStatus> statuses)
    {
        NodeExecutionContext item = NodeTestContext.Create("""{"mode":"once","at":"1970-01-01T00:00:00Z"}""");
        NodeSourceContext source = new(item, () => item, (emission, _) =>
        {
            emissions.Add(emission);
            return ValueTask.CompletedTask;
        }, () => false, (status, _) =>
        {
            statuses.Add(status);
            return ValueTask.CompletedTask;
        }) { Schedule = cursor };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        return await new ScheduleNode().RunAsync(source, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>競合を挟んだ確定状態と保存結果を順に返す境界です。</summary>
    private sealed class ScriptedCursor(IEnumerable<ScheduleCursor> reads, IEnumerable<ScheduleCursor> advances) : INodeScheduleCursor
    {
        private readonly Queue<ScheduleCursor> pendingReads = new(reads);
        private readonly Queue<ScheduleCursor> pendingAdvances = new(advances);

        /// <summary>進行位置を照会した回数です。</summary>
        internal int ReadCount
        {
            get; private set;
        }

        /// <summary>受付で比較した位置の版です。</summary>
        internal List<string?> AdvancedVersions
        {
            get;
        } = [];

        /// <summary>指定した確定位置または照会失敗を返します。</summary>
        /// <returns>次の照会結果です。</returns>
        public ValueTask<ScheduleCursor> ReadAsync(string timelineKey, DateTimeOffset initialAnchor, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return new(pendingReads.Dequeue());
        }

        /// <summary>比較元を観測して次の受付結果を返します。</summary>
        /// <returns>指定した受付成否です。</returns>
        public ValueTask<ScheduleCursor> AdvanceAsync(ScheduleCursor cursor, NodeEmission emission, CancellationToken cancellationToken = default)
        {
            AdvancedVersions.Add(cursor.Version);
            return new(pendingAdvances.Dequeue());
        }

        /// <summary>予定なしの終了はこの試験では発生しません。</summary>
        /// <returns>誤った終了呼出しを検出する例外です。</returns>
        public ValueTask<ScheduleCursor> FinishAsync(ScheduleCursor cursor, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected empty schedule.");
    }
}
