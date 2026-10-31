using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>並列数と要素開始間隔を独立に守り、未開始要素の停止も確認します。</summary>
public sealed class IterationStartIntervalTests
{
    /// <summary>OSタイマーの大きな遅延後も、過去の枠をまとめて開始しません。</summary>
    /// <returns>遅着した350msの次は400msまで新しい要素が始まらないことを確認します。</returns>
    [Fact]
    public async Task LateTimerDoesNotReplayMissedStartsAsync()
    {
        using CancellationTokenSource cancellation = new();
        IterationTestClock clock = new();
        ConcurrentQueue<long> starts = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = IterationTests.Create(async (context, token) =>
        {
            starts.Enqueue(clock.GetTimestamp() / TimeSpan.TicksPerMillisecond);
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NodeResult.Success(context.Inputs["value"]);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, clock);
        Task<ExecutionReport> running = engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(100, "[1,2,3]")), cancellationToken: cancellation.Token);
        try
        {
            await UntilAsync(() => starts.Count == 1 && clock.Pending == 1).ConfigureAwait(true);
            clock.Advance(350);
            await UntilAsync(() => starts.Count == 2 && clock.Pending == 1).ConfigureAwait(true);
            clock.Advance(49);
            Assert.Equal(2, starts.Count);
            clock.Advance(1);
            await UntilAsync(() => starts.Count == 3).ConfigureAwait(true);
            Assert.Equal(new long[] { 0, 350, 400 }, starts);
            release.TrySetResult();
            Assert.Equal(EOutcome.Succeeded, (await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            release.TrySetResult();
            await cancellation.CancelAsync().ConfigureAwait(true);
            await running.ConfigureAwait(true);
        }
    }

    /// <summary>別のWF発火で開始枠の基準や残り時間を共有しません。</summary>
    /// <returns>同じ定義の二回でも0/100msと50/150msへ独立して配列を開始します。</returns>
    [Fact]
    public async Task ConcurrentRunsHaveIndependentStartSlotsAsync()
    {
        using CancellationTokenSource cancellation = new();
        IterationTestClock clock = new();
        ConcurrentDictionary<Guid, ConcurrentQueue<long>> starts = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = IterationTests.Create(async (context, token) =>
        {
            starts.GetOrAdd(context.CausationId, _ => new()).Enqueue(clock.GetTimestamp() / TimeSpan.TicksPerMillisecond);
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NodeResult.Success(context.Inputs["value"]);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, clock);
        ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation.CompiledWorkflow compiled = IterationTests.Compile(fixture, Definition(100, "[1,2]"));
        Task<ExecutionReport> first = engine.ExecuteAsync(compiled, cancellationToken: cancellation.Token);
        Task<ExecutionReport>? second = null;
        try
        {
            await UntilAsync(() => starts.Count == 1 && clock.Pending == 1).ConfigureAwait(true);
            clock.Advance(50);
            second = engine.ExecuteAsync(compiled, cancellationToken: cancellation.Token);
            await UntilAsync(() => starts.Count == 2 && clock.Pending == 2).ConfigureAwait(true);
            clock.Advance(50);
            await UntilAsync(() => starts.Values.Sum(values => values.Count) == 3).ConfigureAwait(true);
            clock.Advance(50);
            await UntilAsync(() => starts.Values.Sum(values => values.Count) == 4).ConfigureAwait(true);
            long[][] times = starts.Values.Select(values => values.ToArray()).OrderBy(values => values[0]).ToArray();
            Assert.Equal(new long[] { 0, 100 }, times[0]);
            Assert.Equal(new long[] { 50, 150 }, times[1]);
            release.TrySetResult();
            Assert.Equal(EOutcome.Succeeded, (await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
            Assert.Equal(EOutcome.Succeeded, (await second!.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            release.TrySetResult();
            await cancellation.CancelAsync().ConfigureAwait(true);
            await Task.WhenAll(first, (Task?)second ?? Task.CompletedTask).ConfigureAwait(true);
        }
    }

    /// <summary>空き枠ができた後は最初の開始からの刻みに合わせ、失った枠をまとめて使いません。</summary>
    /// <returns>0、100、200、400、500msの開始と入力順の集約です。</returns>
    [Fact]
    public async Task StartsAreSpacedFromTheLatestAdmissionAsync()
    {
        using CancellationTokenSource cancellation = new();
        IterationTestClock clock = new();
        ConcurrentDictionary<int, long> starts = new();
        TaskCompletionSource[] finish = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        int active = 0, maximum = 0;
        WorkflowFixture fixture = IterationTests.Create(async (context, _) =>
        {
            int index = context.Inputs["value"].Json.GetInt32();
            starts[index] = clock.GetTimestamp();
            int current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            await finish[index - 1].Task.ConfigureAwait(false);
            Interlocked.Decrement(ref active);
            return NodeResult.Success(context.Inputs["value"]);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, clock);
        Task<ExecutionReport> running = engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(100)), cancellationToken: cancellation.Token);
        try
        {
            await UntilAsync(() => starts.Count == 1 && clock.Pending == 1).ConfigureAwait(true);
            clock.Advance(99);
            Assert.Single(starts);
            clock.Advance(1);
            await UntilAsync(() => starts.Count == 2 && clock.Pending == 1).ConfigureAwait(true);
            clock.Advance(100);
            await UntilAsync(() => starts.Count == 3).ConfigureAwait(true);
            clock.Advance(150);
            Assert.Equal(3, starts.Count);
            finish[2].TrySetResult();
            await UntilAsync(() => clock.Pending == 1).ConfigureAwait(true);
            clock.Advance(25);
            finish[0].TrySetResult();
            clock.Advance(24);
            Assert.Equal(3, starts.Count);
            clock.Advance(1);
            await UntilAsync(() => starts.Count == 4 && clock.Pending == 1).ConfigureAwait(true);
            clock.Advance(99);
            Assert.Equal(4, starts.Count);
            clock.Advance(1);
            await UntilAsync(() => starts.Count == 5).ConfigureAwait(true);
            Assert.Equal(new long[] { 0, 100, 200, 400, 500 }, starts.OrderBy(pair => pair.Key).Select(pair => pair.Value / TimeSpan.TicksPerMillisecond));
            Assert.Equal(3, maximum);
            foreach (TaskCompletionSource item in finish)
            {
                item.TrySetResult();
            }
            ExecutionReport report = await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            Assert.Equal(5, IterationTests.Loop(report).Iteration!.Started);
            Assert.Equal("[1,2,3,4,5]", report.Nodes.Single(node => node.NodeId is "end").Targets.Single().Result.Outputs["value"].Json.GetRawText());
        }
        finally
        {
            foreach (TaskCompletionSource item in finish)
            {
                item.TrySetResult();
            }
            await cancellation.CancelAsync().ConfigureAwait(true);
            await running.ConfigureAwait(true);
        }
    }

    /// <summary>長い間隔の途中でもbreak・失敗・取消で未開始要素を止めます。</summary>
    /// <returns>未開始を実行済みに数えず、開始済み処理の実際の結果を保持します。</returns>
    [Theory]
    [InlineData("break", EOutcome.Succeeded)]
    [InlineData("failure", EOutcome.Failed)]
    [InlineData("cancel", EOutcome.Cancelled)]
    public async Task StopDuringSpacingDoesNotLaunchAnotherElementAsync(string mode, EOutcome expected)
    {
        int calls = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = IterationTests.Create(async (context, _) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task.ConfigureAwait(false);
            return mode switch
            {
                "failure" => NodeResult.Failure("test.failed", "Expected body failure."),
                _ => NodeResult.Success(context.Inputs["value"]),
            };
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(3600000);
        string breakMode = "break", cancelMode = "cancel";
        if (string.Equals(mode, breakMode, StringComparison.Ordinal))
        {
            definition = definition with { Wires = [.. definition.Wires, new("body", CompletionPorts.Signal, "end", "break")] };
        }
        using CancellationTokenSource cancellation = new();
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition), cancellationToken: cancellation.Token);
        try
        {
            await UntilAsync(() => Volatile.Read(ref calls) == 1).ConfigureAwait(true);
            if (string.Equals(mode, cancelMode, StringComparison.Ordinal))
            {
                await cancellation.CancelAsync().ConfigureAwait(true);
                Assert.False(running.IsCompleted);
            }
            release.TrySetResult();
            ExecutionReport report = await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.Equal(expected, report.Outcome);
            Assert.Equal(1, calls);
            Assert.Equal(1, IterationTests.Loop(report).Iteration!.Started);
        }
        finally
        {
            release.TrySetResult();
            await cancellation.CancelAsync().ConfigureAwait(true);
            await running.ConfigureAwait(true);
        }
    }

    /// <summary>設定なしと0は並列開始を維持し、空入力では待機しません。</summary>
    /// <returns>既存WFの互換動作です。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task DisabledSpacingStartsUpToTheConcurrencyLimitAsync(int? interval)
    {
        using CancellationTokenSource cancellation = new();
        int started = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = IterationTests.Create(async (context, token) =>
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NodeResult.Success(context.Inputs["value"]);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(interval)), cancellationToken: cancellation.Token);
        try
        {
            await UntilAsync(() => Volatile.Read(ref started) == 3).ConfigureAwait(true);
            Assert.Equal(3, started);
            release.TrySetResult();
            Assert.Equal(EOutcome.Succeeded, (await running.ConfigureAwait(true)).Outcome);
            ExecutionReport empty = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(3600000, "[]")), cancellationToken: cancellation.Token).ConfigureAwait(true);
            Assert.Equal(0, IterationTests.Loop(empty).Iteration!.Started);
            Assert.Equal(5, started);
        }
        finally
        {
            release.TrySetResult();
            await cancellation.CancelAsync().ConfigureAwait(true);
            await running.ConfigureAwait(true);
        }
    }

    /// <summary>不正な間隔を処理開始前に拒否します。</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("2147483647")]
    public void InvalidIntervalsAreRejected(string interval)
    {
        ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration.ForEachNode node = new(true);
        Assert.NotEmpty(node.Validate(WorkflowFixture.Definition("each", IterationNodeTypes.ParallelForEach, "{\"startIntervalMs\":" + interval + "}")));
    }

    /// <summary>並列数3と指定間隔だけを変えた実グラフです。</summary>
    /// <returns>入力順に結果を集約する反復です。</returns>
    private static WorkflowDefinition Definition(int? interval, string input = "[1,2,3,4,5]")
    {
        WorkflowDefinition definition = IterationTests.Definition(input, parallel: true);
        string settings = interval is null ? "{\"maxConcurrency\":3}"
                                           : "{\"maxConcurrency\":3,\"startIntervalMs\":" + interval.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        string start = "each";
        return definition with { Nodes = definition.Nodes.Select(node => string.Equals(node.Id, start, StringComparison.Ordinal) ? node with { Settings = NodeSettings.Schema(settings) }
                                                                                                                                 : node).ToArray() };
    }

    /// <summary>実行通知やタイマー登録が整うまで有限時間だけ待ちます。</summary>
    /// <returns>次の時計操作を行える状態です。</returns>
    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token).ConfigureAwait(false);
        }
    }
}
