using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>合流前に始まった値を同じ要求から受け取り、別要求の値や副作用を流用しません。</summary>
public sealed class EventContextTests
{
    /// <summary>合流ノード自身の配送直前に届いた取消を、合流処理の失敗へ変換しません。</summary>
    /// <returns>開始されなかった後続と、確認済み取消の報告です。</returns>
    [Fact]
    public async Task CancelledMergeDispatchDoesNotBecomeFailureAsync()
    {
        int calls = 0;
        ValueMergeFixture.Probe probe = new((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success());
        });
        WorkflowFixture fixture = new(probe, new SignalMergeNode("each", new TestStateStore()));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Dispatcher.Handler = (request, token) =>
        {
            string merge = "merge";
            if (string.Equals(request.Node.Id, merge, StringComparison.Ordinal))
            {
                return Task.FromResult(NodeResult.Failure("execution.cancelledBeforeStart", "Cancelled before dispatch", EOutcome.NotExecuted));
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        WorkflowDefinition definition = new("cancel-dispatch", "Cancel dispatch", [Node("source"), Node("merge", "control.signal.each"), Node("sink")],
            [new("source", CompletionPorts.Signal, "merge", "input1"), new("merge", "start", "sink", "trigger")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "source").ConfigureAwait(true);
        Assert.Equal(EOutcome.Cancelled, report.Outcome);
        Assert.Equal(1, calls);
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>入力要求で評価された値も、合流前後で同じ要求の一回分を共有します。</summary>
    /// <returns>時刻や乱数などに相当する動的値を、子実行で再評価しない確認です。</returns>
    [Fact]
    public async Task DemandEvaluatedValueIsSharedAcrossTheEventBoundaryAsync()
    {
        int evaluations = 0;
        ConcurrentQueue<int> observed = new();
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            switch (context.NodeId)
            {
                case "source":
                    return ValueTask.FromResult(NodeResult.Success());
                case "value":
                    return ValueTask.FromResult(ValueMergeFixture.Result(NodeValue.FromObject(Interlocked.Increment(ref evaluations))));
                default:
                    observed.Enqueue(context.Inputs["value"].Json.GetInt32());
                    return ValueTask.FromResult(NodeResult.Success());
            }
        });
        WorkflowFixture fixture = new(probe, new SignalMergeNode("each", new TestStateStore()));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("shared-demand", "Shared demand", [Node("source"), Node("value"), Node("before"), Node("merge", "control.signal.each"), Node("after")],
            [new("source", CompletionPorts.Signal, "before", "trigger"), new("value", "value", "before", "value"),
                new("before", CompletionPorts.Signal, "merge", "input1"), new("merge", "start", "after", "trigger"), new("value", "value", "after", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "source").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(1, evaluations);
        Assert.Equal([1, 1], observed.ToArray());
    }

    /// <summary>開始信号より遅い配列と元の要求値を、初回から反復の後続へ引き継ぎます。</summary>
    /// <returns>要求が追い越しても値が混ざらず、読み込みも一回ずつである確認です。</returns>
    [Theory]
    [InlineData(false, "each")]
    [InlineData(true, "each")]
    [InlineData(false, "all")]
    [InlineData(true, "first")]
    public async Task SignalChildWaitsForItsOwnPendingValuesAsync(bool iterate, string mode)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<(int Request, int Value)> observed = new();
        int reads = 0;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            switch (context.NodeId)
            {
                case "source":
                    return ValueMergeFixture.Result(context.Inputs["value"]);
                case "read":
                    Interlocked.Increment(ref reads);
                    int request = context.Inputs["value"].Json.GetInt32();
                    if (request == 1)
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(token).ConfigureAwait(false);
                    }
                    return ValueMergeFixture.Result(NodeValue.FromObject(new[] { request * 10 }));
                case "sink":
                    System.Text.Json.JsonElement output = context.Inputs["value"].Json;
                    int value = output.ValueKind == System.Text.Json.JsonValueKind.Array ? output[0].GetInt32()
                                                                                         : output.GetInt32();
                    observed.Enqueue((context.Inputs["number"].Json.GetInt32(), value));
                    return NodeResult.Success();
                default:
                    throw new InvalidOperationException(context.NodeId);
            }
        });
        SignalMergeNode merge = new(mode, new TestStateStore());
        WorkflowFixture fixture = new(probe, merge, new ForEachNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = new TestStateStore();
        NodeDefinition mergeDefinition = Node("merge", merge.Descriptor.Type);
        string firstMode = "first";
        if (string.Equals(mode, firstMode, StringComparison.Ordinal))
        {
            mergeDefinition = mergeDefinition with { Settings = System.Text.Json.JsonSerializer.SerializeToElement(new { scope = "events" }) };
        }
        List<NodeDefinition> nodes = [Node("source"), Node("read"), mergeDefinition, Node("sink")];
        List<WireDefinition> wires = [new("source", "value", "read", "value"), new("source", CompletionPorts.Signal, "read", "trigger"),
            new("source", CompletionPorts.Signal, "merge", "input1"), new("source", "value", "sink", "number")];
        if (iterate)
        {
            nodes.Add(Node("loop", IterationNodeTypes.ForEach));
            wires.AddRange([new("merge", "start", "loop", "trigger"), new("read", "value", "loop", "value"), new("loop", "item", "sink", "value")]);
        }
        else
        {
            wires.AddRange([new("merge", "start", "sink", "trigger"), new("read", "value", "sink", "value")]);
        }
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, new("causal-values", "Causal values", nodes, wires));
        Task<ExecutionReport> first = fixture.Engine.ExecuteAsync(workflow, "source", Input(1), cancellationToken: timeout.Token);
        try
        {
            await started.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            ExecutionReport second = await fixture.Engine.ExecuteAsync(workflow, "source", Input(2), cancellationToken: timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, second.Outcome);
            Assert.Equal((2, 20), Assert.Single(observed));
            Assert.False(first.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await first.ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, (await first.ConfigureAwait(true)).Outcome);
        Assert.Equal(new[] { (2, 20), (1, 10) }, observed.ToArray());
        Assert.Equal(2, reads);
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>並列枠以上の合流が同じ遅い供給元を待っても、供給元を枠待ちで止めません。</summary>
    /// <returns>二段の合流でも同じ要求の値を受け取り、副作用は一回だけである確認です。</returns>
    [Fact]
    public async Task ManyChildrenCanAwaitTheSameProducerAcrossTwoMergesAsync()
    {
        const int count = 24;
        int reads = 0;
        ConcurrentQueue<int> observed = new();
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            switch (context.NodeId)
            {
                case "source":
                    return ValueTask.FromResult(ValueMergeFixture.Result(NodeValue.FromObject(42)));
                case "read":
                    Interlocked.Increment(ref reads);
                    return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
                default:
                    observed.Enqueue(context.Inputs["value"].Json.GetInt32());
                    return ValueTask.FromResult(NodeResult.Success());
            }
        });
        WorkflowFixture fixture = new(probe, new SignalMergeNode("each", new TestStateStore()));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        List<NodeDefinition> nodes = [Node("source"), Node("read")];
        List<WireDefinition> wires = [new("source", "value", "read", "value")];
        for (int index = 0; index < count; index++)
        {
            string suffix = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string first = "merge-" + suffix, second = "next-" + suffix, sink = "sink-" + suffix;
            nodes.AddRange([Node(first, "control.signal.each"), Node(second, "control.signal.each"), Node(sink)]);
            wires.AddRange([new("source", CompletionPorts.Signal, first, "input1"), new(first, "start", second, "input1"),
                new(second, "start", sink, "trigger"), new("read", "value", sink, "value")]);
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, new("many-children", "Many children", nodes, wires)), "source", cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(count, observed.Count);
        Assert.All(observed, value => Assert.Equal(42, value));
        Assert.Equal(1, reads);
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>元の値取得が失敗したときは、成功した別要求の保持値を代入しません。</summary>
    /// <returns>後続を実行せず、実際の失敗を報告する確認です。</returns>
    [Fact]
    public async Task FailedCurrentValueDoesNotUsePreviousRetainedResultAsync()
    {
        int calls = 0;
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            switch (context.NodeId)
            {
                case "source":
                    return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
                case "read":
                    int value = context.Inputs["value"].Json.GetInt32();
                    NodeResult result = value == 1 ? ValueMergeFixture.Result(NodeValue.FromObject(10))
                                                   : NodeResult.Failure("test.readFailed", "No current value");
                    return ValueTask.FromResult(result);
                default:
                    Interlocked.Increment(ref calls);
                    Assert.Equal(10, context.Inputs["value"].Json.GetInt32());
                    return ValueTask.FromResult(NodeResult.Success());
            }
        });
        WorkflowFixture fixture = new(probe, new SignalMergeNode("each", new TestStateStore()));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = new TestStateStore();
        WorkflowDefinition definition = new("failed-input", "Failed input", [Node("source"), Node("read"), Node("merge", "control.signal.each"), Node("sink")],
            [new("source", "value", "read", "value"), new("source", CompletionPorts.Signal, "read", "trigger"),
                new("source", CompletionPorts.Signal, "merge", "input1"), new("merge", "start", "sink", "trigger"), new("read", "value", "sink", "value")]);
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Engine.ExecuteAsync(workflow, "source", Input(1)).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Failed, (await fixture.Engine.ExecuteAsync(workflow, "source", Input(2)).ConfigureAwait(true)).Outcome);
        Assert.Equal(1, calls);
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>値待ちの子実行も元要求の明示取消に従い、取消後の副作用を開始しません。</summary>
    /// <returns>開始済みの読み込みを回収し、所有中の実行を残さない確認です。</returns>
    [Fact]
    public async Task CancellationStopsChildrenWaitingForParentValuesAsync()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            switch (context.NodeId)
            {
                case "source":
                    return ValueMergeFixture.Result(NodeValue.FromObject(42));
                case "read":
                    started.TrySetResult();
                    try
                    {
                        await release.Task.WaitAsync(token).ConfigureAwait(false);
                        return ValueMergeFixture.Result(context.Inputs["value"]);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return NodeResult.Failure("test.cancelled", "Stopped", EOutcome.Cancelled);
                    }
                default:
                    Interlocked.Increment(ref calls);
                    return NodeResult.Success();
            }
        });
        WorkflowFixture fixture = new(probe, new SignalMergeNode("each", new TestStateStore()));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Dispatcher.Handler = (request, _) => fixture.Executor.ExecuteAsync(request, CancellationToken.None);
        fixture.Engine.NodeCancellationHandler = async (request, token) => await fixture.Executor.RequestCancellationAsync(request.Id, request.ExpiresAt, token).ConfigureAwait(false);
        WorkflowDefinition definition = new("cancel-input", "Cancel input", [Node("source"), Node("read"), Node("merge", "control.signal.each"), Node("sink")],
            [new("source", "value", "read", "value"), new("source", CompletionPorts.Signal, "merge", "input1"),
                new("merge", "start", "sink", "trigger"), new("read", "value", "sink", "value")]);
        Guid parent = Guid.NewGuid();
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "source", executionId: parent);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.True(await fixture.Engine.RequestCancellationAsync(parent).ConfigureAwait(true));
            ExecutionReport report = await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.True(report.Outcome == EOutcome.Cancelled, System.Text.Json.JsonSerializer.Serialize(report));
        }
        finally
        {
            release.TrySetResult();
            await running.ConfigureAwait(true);
        }
        Assert.Equal(0, calls);
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>独立した一件の入口へ番号を渡します。</summary>
    /// <returns>要求ごとの入力です。</returns>
    private static Dictionary<string, NodeValue> Input(int value) => new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) };

    /// <summary>試験の有限処理を定義します。</summary>
    /// <returns>空設定のノードです。</returns>
    private static NodeDefinition Node(string id, string type = "test.value") => WorkflowFixture.Definition(id, type);
}
