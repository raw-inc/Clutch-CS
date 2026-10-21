using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>制限区間へ値と開始事象が別々に届く配線を確認します。</summary>
public sealed class ConcurrencyEventScopeTests
{
    private static readonly int[] elements = [1, 2, 3];

    /// <summary>開始信号が別経路でも、制限区間の配列を同じ要求の反復へ渡します。</summary>
    /// <returns>偽の失敗がなく、全要素を一度だけ処理した結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateSignalStartsLoopWithLimitedValueAsync(bool end)
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        ConcurrentQueue<int> received = new();
        int sideCalls = 0;
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            const string source = "source";
            if (string.Equals(context.NodeId, source, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
            }
            Assert.Equal(1, limiter.Active);
            switch (context.NodeId)
            {
                case "side": Interlocked.Increment(ref sideCalls); break;
                default: received.Enqueue(context.Inputs["value"].Json.GetInt32()); break;
            }
            return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
        });
        WorkflowFixture fixture = Create(probe, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompiledWorkflow workflow = IterationTests.Compile(fixture, Definition(end));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(workflow, "source",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(elements) }).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal([1, 2, 3], received.Order());
        Assert.Equal(1, sideCalls);
        Assert.True(0 < limiter.Released);
        Assert.Equal(0, limiter.Active);
    }

    /// <summary>別要求から遅れて届く事象も制限し、配列と起動信号を混線させません。</summary>
    /// <returns>実処理中の要求は一つだけで、すべての入力が一度ずつ到達します。</returns>
    [Fact]
    public async Task ConcurrentSeparateSignalsRemainWithinOnePermitAsync()
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        ConcurrentDictionary<int, int> running = new();
        ConcurrentQueue<int> received = new();
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            const string body = "body";
            if (string.Equals(context.NodeId, body, StringComparison.Ordinal))
            {
                Assert.Equal(1, limiter.Active);
                int value = context.Inputs["value"].Json.GetInt32();
                int owner = value / 10;
                running.AddOrUpdate(owner, 1, (_, count) => count + 1);
                Assert.Single(running);
                await Task.Delay(20, token).ConfigureAwait(false);
                received.Enqueue(value);
                if (running.AddOrUpdate(owner, 0, (_, count) => count - 1) == 0)
                {
                    running.TryRemove(owner, out _);
                }
            }
            return ValueMergeFixture.Result(context.Inputs["value"]);
        });
        WorkflowFixture fixture = Create(probe, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompiledWorkflow workflow = IterationTests.Compile(fixture, Definition(true));
        ExecutionReport[] reports = await Task.WhenAll(Enumerable.Range(1, 3).Select(owner => fixture.Engine.ExecuteAsync(workflow, "source",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(Enumerable.Range(owner * 10, 3).ToArray()) }))).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        Assert.All(reports, report => Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report)));
        Assert.Equal([10, 11, 12, 20, 21, 22, 30, 31, 32], received.Order());
        Assert.Empty(running);
        Assert.Equal(0, limiter.Active);
    }

    /// <summary>後着事象の実処理が失敗した場合は、待機と区別して理由を残します。</summary>
    /// <returns>実失敗を成功扱いせず、制限枠を回収します。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, EOutcome.Failed)]
    [InlineData(EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData(EOutcome.Cancelled, EOutcome.Failed)]
    public async Task SeparateSignalFailureIsReportedAndReleasesPermitAsync(EOutcome outcome, EOutcome expected)
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            const string body = "body";
            if (string.Equals(context.NodeId, body, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(NodeResult.Failure("fixture.body", "Isolated downstream failure", outcome));
            }
            return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
        });
        WorkflowFixture fixture = Create(probe, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(true)), "source",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(elements) }).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        Assert.True(report.Outcome == expected, JsonSerializer.Serialize(report));
        Assert.Contains(fixture.Engine.Recent().SelectMany(result => result.Nodes), node => node.NodeId is "limit"
            && node.Targets.Any(target => target.Result.ReasonCode is "concurrency.body" && !string.IsNullOrEmpty(target.Result.Message)));
        Assert.Equal(0, limiter.Active);
    }

    /// <summary>別経路の事象で開始した処理も、要求の取消後に枠を残しません。</summary>
    /// <returns>開始済み処理の終了と、すべての枠の回収です。</returns>
    [Fact]
    public async Task SeparateSignalCancellationReturnsPermitAsync()
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            const string body = "body";
            if (string.Equals(context.NodeId, body, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Assert.Equal(1, limiter.Active);
                    return NodeResult.Failure("fixture.cancelled", "Stopped", EOutcome.Cancelled);
                }
            }
            return ValueMergeFixture.Result(context.Inputs["value"]);
        });
        WorkflowFixture fixture = Create(probe, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(true)), "source",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(elements) }, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await cancellation.CancelAsync().ConfigureAwait(true);
        ExecutionReport report = await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Cancelled, JsonSerializer.Serialize(fixture.Engine.Recent()));
        Assert.Equal(0, limiter.Active);
    }

    /// <summary>取消で副作用を開始しなかった要素を、実失敗や結果不明と区別して集約します。</summary>
    /// <returns>取消の有無に応じた一件の結果と、元の対象別結果・枠の解放です。</returns>
    [Theory]
    [InlineData(true, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(true, "execution.cancelledBeforeStart", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(true, "overlap.cancelled", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(true, "source.cancelledBeforeExecution", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(false, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(false, "execution.cancelledBeforeStart", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(false, "overlap.cancelled", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(false, "source.cancelledBeforeExecution", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(true, "overlap.running", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(true, "fixture.blocked", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(true, "execution.cancelUnconfirmed", EOutcome.Failed, EOutcome.Failed)]
    [InlineData(true, "execution.cancelUnconfirmed", EOutcome.Unknown, EOutcome.Unknown)]
    public async Task CancellationKeepsUnstartedBodiesDistinctFromFailureAsync(bool cancel, string reason, EOutcome bodyOutcome, EOutcome expected)
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        using CancellationTokenSource cancellation = new();
        ValueMergeFixture.Probe probe = new(async (context, _) =>
        {
            const string body = "body";
            if (string.Equals(context.NodeId, body, StringComparison.Ordinal))
            {
                if (cancel)
                {
                    await cancellation.CancelAsync().ConfigureAwait(false);
                }
                return NodeResult.Failure(reason, "Fixture operation did not complete.", bodyOutcome);
            }
            return ValueMergeFixture.Result(context.Inputs["value"]);
        });
        WorkflowFixture fixture = Create(probe, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Definition(true)), "source",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(elements) }, cancellationToken: cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        Assert.True(report.Outcome == expected, JsonSerializer.Serialize(fixture.Engine.Recent()));
        IterationExecutionSummary iteration = Assert.Single(fixture.Engine.Recent().SelectMany(result => result.Nodes).Select(node => node.Iteration).OfType<IterationExecutionSummary>());
        Assert.Contains(iteration.Items.SelectMany(item => Assert.IsType<ExecutionReport>(item.Execution.Report).Nodes).SelectMany(node => node.Targets),
            target => string.Equals(target.Result.ReasonCode, reason, StringComparison.Ordinal) && target.Result.Outcome == bodyOutcome);
        Assert.Equal(0, limiter.Active);
    }

    /// <summary>実際の事象配送・区間判定と、観測可能な制限枠を接続します。</summary>
    /// <returns>外部APIや本番保存先を持たない実行器です。</returns>
    private static WorkflowFixture Create(ValueMergeFixture.Probe probe, ConcurrencyWorkflowTests.ProbeLimiter limiter)
    {
        WorkflowFixture fixture = new(probe, new ConcurrencyLimitNode(), new ConcurrencyLimitNode(true), new SignalMergeNode("each", new TestStateStore()), new ForEachNode(true), new ForEachEndNode());
        fixture.Engine.ConcurrencyLimiter = limiter;
        return fixture;
    }

    /// <summary>開始待ちの先行評価から、終了通知だけが先に発火しないことを確認します。</summary>
    /// <returns>反復終了後に一度だけ発火する通知です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionSignalWaitsForSeparateEventAsync(bool end)
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        int bodies = 0, completed = 0;
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            switch (context.NodeId)
            {
                case "body": Interlocked.Increment(ref bodies); break;
                case "completed":
                    Assert.Equal(3, Volatile.Read(ref bodies));
                    Assert.Equal(0, limiter.Active);
                    Interlocked.Increment(ref completed);
                    return ValueTask.FromResult(NodeResult.Success());
            }
            return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
        });
        WorkflowFixture fixture = Create(probe, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(end);
        definition = definition with { Nodes = [.. definition.Nodes, WorkflowFixture.Definition("completed", "test.value")],
            Wires = [.. definition.Wires, new(end ? "limit-end"
                                                  : "limit", CompletionPorts.Signal, "completed", "trigger")] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition), "source",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(elements) }).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(1, completed);
        Assert.Equal(0, limiter.Active);
    }

    /// <summary>値の制限経路と、区間外の開始信号経路を組み立てます。</summary>
    /// <returns>副作用を持たない模擬生成ノードへ届く実配線です。</returns>
    private static WorkflowDefinition Definition(bool end)
    {
        WorkflowDefinition definition = new("limited-events", "Limited events",
            [WorkflowFixture.Definition("source", "test.value"), WorkflowFixture.Definition("limit", ConcurrencyNodeTypes.Start),
                WorkflowFixture.Definition("signal", "control.signal.each"), WorkflowFixture.Definition("each", IterationNodeTypes.ParallelForEach),
                WorkflowFixture.Definition("body", "test.value"), WorkflowFixture.Definition("side", "test.value"), WorkflowFixture.Definition("end", IterationNodeTypes.End)],
            [new("source", "value", "limit", "value"), new("source", CompletionPorts.Signal, "signal", "input1"),
                new("limit", "value", "each", "value"), new("signal", "start", "each", "trigger"), new("each", "item", "body", "value"),
                new("limit", "value", "side", "value"), new("each", "loop", "end", "loop"), new("body", "value", "end", "value")]);
        if (end)
        {
            definition = definition with { Nodes = [.. definition.Nodes, WorkflowFixture.Definition("limit-end", ConcurrencyNodeTypes.End)],
                Wires = [.. definition.Wires, new("limit", "scope", "limit-end", "scope"), new("end", "value", "limit-end", "value")] };
        }
        return definition;
    }
}
