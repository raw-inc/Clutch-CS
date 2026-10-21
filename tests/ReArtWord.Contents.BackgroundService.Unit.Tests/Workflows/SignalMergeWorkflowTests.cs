using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>別々の一件にまたがる合流と、待っていない入力に阻まれない発火を確認します。</summary>
public sealed class SignalMergeWorkflowTests
{
    private static readonly int[] elements = [1, 2, 3];

    /// <summary>元入力の明示取消を、合流から開始した別実行の処理にも配送します。</summary>
    /// <returns>子実行を置き去りにしない取消です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingParentAlsoCancelsStartedMergeChildAsync(bool scopedFirst)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe sink = new("test.sink", async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                return NodeResult.Success();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return NodeResult.Failure("test.cancelled", "Stopped", EOutcome.Cancelled);
            }
        });
        string mode = scopedFirst ? "first"
                                  : "each";
        SignalMergeNode merge = new(mode, new TestStateStore());
        WorkflowFixture fixture = new(new IfNode(), merge, sink);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Dispatcher.Handler = (request, _) => fixture.Executor.ExecuteAsync(request, CancellationToken.None);
        fixture.Engine.NodeCancellationHandler = async (request, token) => await fixture.Executor.RequestCancellationAsync(request.Id, request.ExpiresAt, token).ConfigureAwait(false);
        NodeDefinition mergeNode = Node("merge", merge.Descriptor.Type);
        if (scopedFirst)
        {
            mergeNode = mergeNode with { Settings = JsonSerializer.SerializeToElement(new { }) };
        }
        WorkflowDefinition definition = new("cancel-merge", "Signals", [Gate("a"), mergeNode, Node("sink", sink.Descriptor.Type)],
            [new("a", "start", "merge", "input1"), new("merge", "start", "sink", "trigger")]);
        Guid parent = Guid.NewGuid();
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(Compile(fixture, definition), "a", executionId: parent);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.True(await fixture.Engine.RequestCancellationAsync(parent).ConfigureAwait(true));
            Assert.Equal(EOutcome.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            release.TrySetResult();
            await running.ConfigureAwait(true);
        }
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>保存結果不明は版競合の理由を含んでも再送せず、後続を開始しません。</summary>
    /// <returns>合流状態が確認できない場合の障害境界です。</returns>
    [Fact]
    public async Task UnconfirmedMergeDoesNotRetryOrEmitAsync()
    {
        int calls = 0;
        ConflictingStateStore store = new() { UnknownWrites = true };
        SignalMergeNode merge = new("all", store);
        Probe sink = new("test.sink", (_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        });
        WorkflowFixture fixture = new(new IfNode(), merge, sink);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("unconfirmed-merge", "Signals", [Gate("a"), Node("merge", merge.Descriptor.Type), Node("sink", sink.Descriptor.Type)],
            [new("a", "start", "merge", "input1"), new("merge", "start", "sink", "trigger")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, definition), "a").ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, report.Outcome);
        Assert.Equal(1, store.WriteAttempts);
        Assert.Equal(0, calls);
    }

    /// <summary>都度・一巡の先着・全入力を、同じ入力への重複も含めて区別します。</summary>
    /// <returns>別実行の信号を保持して発火する回数です。</returns>
    [Theory]
    [InlineData("each", 1, 2, 3, 4, 5)]
    [InlineData("first", 1, 1, 1, 2, 2)]
    [InlineData("all", 0, 0, 1, 1, 2)]
    public async Task MergesAcrossExecutionsAndResetsAfterOneRoundAsync(string mode, int first, int second, int third, int fourth, int fifth)
    {
        int calls = 0;
        ConflictingStateStore store = new();
        SignalMergeNode merge = new(mode, store);
        Probe sink = new("test.sink", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success());
        });
        WorkflowFixture fixture = new(new IfNode(), merge, sink);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("signals", "Signals", [Gate("a"), Gate("b"), Node("merge", merge.Descriptor.Type), Node("sink", sink.Descriptor.Type)],
            [new("a", "start", "merge", "input1"), new("b", "start", "merge", "input2"), new("merge", "start", "sink", "trigger")]);
        CompiledWorkflow workflow = Compile(fixture, definition);
        string[] entries = ["a", "a", "b", "b", "a"];
        int[] expected = [first, second, third, fourth, fifth];
        for (int index = 0; index < entries.Length; index++)
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(workflow, entries[index]).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            Assert.Equal(expected[index], calls);
        }
    }

    /// <summary>先着または都度方式は、別入力の遅い処理を待たず後続を開始します。</summary>
    /// <returns>時刻の推測ではなく同期点で確認した先着発火です。</returns>
    [Theory]
    [InlineData("each", 2, false)]
    [InlineData("first", 1, false)]
    [InlineData("first", 1, true)]
    public async Task FirstSignalStartsDownstreamBeforeOtherInputCompletesAsync(string mode, int expected, bool scopedFirst)
    {
        int calls = 0;
        TaskCompletionSource slow = new(TaskCreationOptions.RunContinuationsAsynchronously), started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Probe fast = new("test.fast", (_, _) => ValueTask.FromResult(NodeResult.Success()));
        Probe delayed = new("test.slow", async (_, token) =>
        {
            await slow.Task.WaitAsync(token).ConfigureAwait(false);
            return NodeResult.Success();
        });
        Probe sink = new("test.sink", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            return ValueTask.FromResult(NodeResult.Success());
        });
        SignalMergeNode merge = new(mode, new TestStateStore());
        WorkflowFixture fixture = new(fast, delayed, sink, merge);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition mergeNode = Node("merge", merge.Descriptor.Type);
        if (scopedFirst)
        {
            mergeNode = mergeNode with { Settings = JsonSerializer.SerializeToElement(new { }) };
        }
        WorkflowDefinition definition = new("racing-signals", "Signals", [Node("fast", fast.Descriptor.Type), Node("slow", delayed.Descriptor.Type), mergeNode, Node("sink", sink.Descriptor.Type)],
            [new("fast", CompletionPorts.Signal, "merge", "input1"), new("slow", CompletionPorts.Signal, "merge", "input2"), new("merge", "start", "sink", "trigger")]);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(Compile(fixture, definition));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.False(running.IsCompleted);
            Assert.Equal(1, calls);
        }
        finally
        {
            slow.TrySetResult();
        }
        Assert.Equal(EOutcome.Succeeded, (await running.ConfigureAwait(true)).Outcome);
        Assert.Equal(expected, calls);
    }

    /// <summary>反復の各開始を新しい合流実行へ渡し、反復全体の完了と混同しません。</summary>
    /// <returns>逐次・並列のどちらでも要素数だけ実行した確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachElementCanEmitAcrossTheIterationBoundaryAsync(bool parallel)
    {
        int calls = 0;
        ForEachNode loop = new(parallel);
        SignalMergeNode merge = new("each", new TestStateStore());
        Probe sink = new("test.sink", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success());
        });
        WorkflowFixture fixture = new(loop, merge, sink);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition start = Node("loop", loop.Descriptor.Type) with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(elements) } };
        WorkflowDefinition definition = new("loop-signals", "Loop signals", [start, Node("merge", merge.Descriptor.Type), Node("sink", sink.Descriptor.Type)],
            [new("loop", "start", "merge", "input1"), new("merge", "start", "sink", "trigger")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, definition), "loop").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(3, calls);
    }

    /// <summary>実配線を保存前検証へ渡します。</summary>
    /// <returns>問題のない計画です。</returns>
    private static CompiledWorkflow Compile(WorkflowFixture fixture, WorkflowDefinition definition)
    {
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        return compiled.Workflow!;
    }

    /// <summary>外から明示開始できる真の条件です。</summary>
    /// <returns>開始信号の入口です。</returns>
    private static NodeDefinition Gate(string id) => Node(id, "control.ifStart") with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["condition"] = NodeValue.FromObject(true) } };

    /// <summary>空設定の有限ノードです。</summary>
    /// <returns>試験する定義です。</returns>
    private static NodeDefinition Node(string id, string type)
    {
        string first = "control.signal.first";
        string settings = "{}";
        if (string.Equals(type, first, StringComparison.Ordinal))
        {
            settings = """{"scope":"events"}""";
        }
        return new(id, type, JsonSerializer.Deserialize<JsonElement>(settings));
    }

    /// <summary>発火した副作用だけを数えます。</summary>
    private sealed class Probe(string type, Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> action) : IActionNode
    {
        /// <summary>通常の有限実行として登録します。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe(type, type, "Test", "{}", []) with { HasAsyncCompletion = true };

        /// <summary>試験ノードには設定がありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>実配送された処理だけ呼びます。</summary>
        /// <returns>処理の確認結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => action(context, cancellationToken);
    }
}
