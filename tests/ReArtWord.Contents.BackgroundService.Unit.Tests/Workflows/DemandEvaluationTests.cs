using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>要求時評価・一件内の共有・明示信号後の保持を実グラフで確認します。</summary>
public sealed class DemandEvaluationTests
{
    private static readonly string[] elements = ["a", "b", "c"];

    /// <summary>同時に値を要求しても一回だけ実行し、次の受信では再評価します。</summary>
    /// <returns>多重配送しない値供給です。</returns>
    [Fact]
    public async Task ConcurrentConsumersShareOneValuePerExecutionAsync()
    {
        int calls = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeNode probe = new(async (_, token) =>
        {
            int value = Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NodeResult.Success(NodeValue.FromObject(value));
        });
        WorkflowFixture fixture = new(probe, new IfNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("demand", "Demand", [Node("value", probe.Descriptor.Type), Gate("read"), Node("first", "control.pass"), Node("second", "control.pass")],
            [new("read", "start", "first", "trigger"), new("read", "start", "second", "trigger"), new("value", "value", "first", "value"), new("value", "value", "second", "value")]);
        CompiledWorkflow workflow = Compile(fixture, definition);
        Assert.Equal(0, calls);
        Task<ExecutionReport> pending = fixture.Engine.ExecuteAsync(workflow, "read");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        Assert.Equal(1, calls);
        release.SetResult();
        ExecutionReport first = await pending.ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, first.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(1, Result(first, "first").Outputs["value"].Json.GetInt32());
        Assert.Equal(1, Result(first, "second").Outputs["value"].Json.GetInt32());
        ExecutionReport second = await fixture.Engine.ExecuteAsync(workflow, "read").ConfigureAwait(true);
        Assert.Equal(2, calls);
        Assert.Equal(2, Result(second, "second").Outputs["value"].Json.GetInt32());
    }

    /// <summary>開始信号付きのノードは初回信号まで未確定で、別実行の参照では再実行しません。</summary>
    /// <returns>更新と参照を分けた結果保持です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignaledNodesRetainValuesAcrossExecutionsAsync(bool replicatedConflict)
    {
        int calls = 0;
        ProbeNode probe = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(++calls))));
        WorkflowFixture fixture = new(probe, new IfNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = replicatedConflict ? new ConflictingStateStore()
                                                           : new TestStateStore();
        WorkflowDefinition definition = new("retained", "Retained", [Node("value", probe.Descriptor.Type), Gate("update"), Gate("read"), Node("consumer", "control.pass")],
            [new("update", "start", "value", "trigger"), new("read", "start", "consumer", "trigger"), new("value", "value", "consumer", "value")]);
        CompiledWorkflow workflow = Compile(fixture, definition);
        ExecutionReport empty = await fixture.Engine.ExecuteAsync(workflow, "read").ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, Result(empty, "consumer").Outcome);
        Assert.Equal(0, calls);
        await fixture.Engine.ExecuteAsync(workflow, "update").ConfigureAwait(true);
        Assert.Equal(1, calls);
        for (int index = 0; index < 2; index++)
        {
            ExecutionReport read = await fixture.Engine.ExecuteAsync(workflow, "read").ConfigureAwait(true);
            Assert.Equal(1, Result(read, "consumer").Outputs["value"].Json.GetInt32());
            Assert.Equal(1, calls);
        }
        await fixture.Engine.ExecuteAsync(workflow, "update").ConfigureAwait(true);
        ExecutionReport changed = await fixture.Engine.ExecuteAsync(workflow, "read").ConfigureAwait(true);
        Assert.Equal(2, calls);
        Assert.Equal(2, Result(changed, "consumer").Outputs["value"].Json.GetInt32());
    }

    /// <summary>開始世代の保存が不明なら、副作用も保存の再送も行いません。</summary>
    /// <returns>競合と結果不明を区別した未実行です。</returns>
    [Fact]
    public async Task UnconfirmedGenerationDoesNotRunOrRetryAsync()
    {
        int calls = 0;
        ProbeNode probe = new((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(++calls))));
        WorkflowFixture fixture = new(probe, new IfNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ConflictingStateStore store = new() { UnknownWrites = true };
        fixture.Engine.RetainedValues = store;
        WorkflowDefinition definition = new("unconfirmed-retention", "Retention", [Node("value", probe.Descriptor.Type), Gate("update")],
            [new("update", "start", "value", "trigger")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, definition), "update").ConfigureAwait(true);
        Assert.Equal(0, calls);
        Assert.Equal(1, store.WriteAttempts);
        Assert.Equal(EOutcome.NotExecuted, Result(report, "value").Outcome);
    }

    /// <summary>遅い旧実行で、新しい開始信号が確定した値を上書きしません。</summary>
    /// <returns>同じ版と設定変更を挟む版の両方での保持境界です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCompletionCannotOverwriteANewerSignalAsync(bool newRevision)
    {
        int calls = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeNode probe = new(async (_, token) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            return NodeResult.Success(NodeValue.FromObject(call));
        });
        WorkflowFixture fixture = new(probe, new IfNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = new TestStateStore();
        WorkflowDefinition definition = new("late-retained", "Retained", [Node("value", probe.Descriptor.Type), Gate("update"), Gate("read"), Node("consumer", "control.pass")],
            [new("update", "start", "value", "trigger"), new("read", "start", "consumer", "trigger"), new("value", "value", "consumer", "value")]);
        CompiledWorkflow old = Compile(fixture, definition);
        CompiledWorkflow current = old;
        if (newRevision)
        {
            current = Compile(fixture, definition with { Name = "New revision" });
        }
        Task<ExecutionReport> first = fixture.Engine.ExecuteAsync(old, "update");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            ExecutionReport pending = await fixture.Engine.ExecuteAsync(old, "read").ConfigureAwait(true);
            Assert.Equal(EOutcome.NotExecuted, Result(pending, "consumer").Outcome);
            await fixture.Engine.ExecuteAsync(current, "update").ConfigureAwait(true);
            ExecutionReport latest = await fixture.Engine.ExecuteAsync(current, "read").ConfigureAwait(true);
            Assert.Equal(2, Result(latest, "consumer").Outputs["value"].Json.GetInt32());
        }
        finally
        {
            release.TrySetResult();
            await first.ConfigureAwait(true);
        }
        ExecutionReport after = await fixture.Engine.ExecuteAsync(current, "read").ConfigureAwait(true);
        Assert.Equal(2, Result(after, "consumer").Outputs["value"].Json.GetInt32());
        Assert.Equal(2, calls);
    }

    /// <summary>反復だけでは日付等の値を再評価せず、変わる要素の値は正しく結合します。</summary>
    /// <returns>一件の共有と反復ごとの入力分離です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IterationsShareUnchangedDependenciesButUseTheirOwnElementsAsync(bool eachElement, bool parallel)
    {
        int calls = 0;
        ProbeNode probe = new((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("root/")));
        });
        ForEachNode iteration = new(parallel);
        WorkflowFixture fixture = new(probe, iteration, new ForEachEndNode(), new ConcatNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition loop = Node("loop", iteration.Descriptor.Type) with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(elements) } };
        WorkflowDefinition definition = new("iteration-demand", "Iteration demand", [Node("value", probe.Descriptor.Type), loop, Node("concat", "data.concat"), Node("end", "control.forEach.end")],
            [new("value", "value", "concat", "input1"), new("loop", "item", "concat", "input2"), new("loop", "loop", "end", "loop"), new("concat", "value", "end", "value")]);
        if (eachElement)
        {
            definition = definition with { Wires = [.. definition.Wires, new("loop", "start", "value", "trigger")] };
        }
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, definition), "loop").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(eachElement ? 3
                                 : 1, calls);
        Assert.Equal(["root/a", "root/b", "root/c"], Result(report, "end").Outputs["value"].Json.EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>結果不明の要求を他の接続先のために暗黙再実行しません。</summary>
    /// <returns>同じ一件の障害境界です。</returns>
    [Fact]
    public async Task UnknownDemandIsNotRetriedForAnotherConsumerAsync()
    {
        int calls = 0;
        ProbeNode probe = new((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Failure("test.unconfirmed", "Unconfirmed", EOutcome.Unknown));
        });
        WorkflowFixture fixture = new(probe, new IfNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("unknown-demand", "Unknown", [Node("value", probe.Descriptor.Type), Gate("read"), Node("a", "control.pass"), Node("b", "control.pass")],
            [new("read", "start", "a", "trigger"), new("read", "start", "b", "trigger"), new("value", "value", "a", "value"), new("value", "value", "b", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, definition), "read").ConfigureAwait(true);
        Assert.Equal(1, calls);
        Assert.Equal(EOutcome.Unknown, report.Outcome);
        Assert.Equal(EOutcome.NotExecuted, Result(report, "a").Outcome);
        Assert.Equal(EOutcome.NotExecuted, Result(report, "b").Outcome);
    }

    /// <summary>コンパイル時にノードの実行を要求しません。</summary>
    /// <returns>検証済みの計画です。</returns>
    private static CompiledWorkflow Compile(WorkflowFixture fixture, WorkflowDefinition definition)
    {
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        return compiled.Workflow!;
    }

    /// <summary>一つの実行対象の結果を読みます。</summary>
    /// <returns>該当ノードの出力です。</returns>
    private static NodeResult Result(ExecutionReport report, string id) => Assert.Single(report.Nodes.Single(node => string.Equals(node.NodeId, id, StringComparison.Ordinal)).Targets).Result;

    /// <summary>明示開始信号を発生する入口です。</summary>
    /// <returns>条件がtrueのIf-Startです。</returns>
    private static NodeDefinition Gate(string id) => Node(id, "control.ifStart") with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["condition"] = NodeValue.FromObject(true) } };

    /// <summary>空設定のノードを作ります。</summary>
    /// <returns>検証対象です。</returns>
    private static NodeDefinition Node(string id, string type) => new(id, type, JsonSerializer.SerializeToElement(new { }));

    /// <summary>副作用の実行回数と結果を制御する有限ノードです。</summary>
    private sealed class ProbeNode(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> run) : IActionNode
    {
        /// <summary>純粋値ではない通常の有限処理です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.demand", "Demand", "Test", "{}", []);

        /// <summary>設定を使わない試験ノードです。</summary>
        /// <returns>検証問題なし。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>実際に配送された呼出しだけ数えます。</summary>
        /// <returns>試験で指定した結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => run(context, cancellationToken);
    }
}
