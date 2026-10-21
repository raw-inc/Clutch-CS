using System.Collections.Concurrent;
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

/// <summary>共通準備と反復の配線を変えずに、要求・要素を分離して開始することを確認します。</summary>
public sealed class ScopedSignalMergeTests
{
    private static readonly bool[] conditions = [true, false, false, true];

    /// <summary>同じ枝が連続しても、共通の信号で全要素を一回ずつ開始します。</summary>
    /// <returns>既存・新規作成、逐次・並列、同一要素の独立性です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CommonPreparationRunsEveryElementOnceAsync(bool exists, bool parallel)
    {
        ConcurrentBag<(Guid Cause, Guid Execution, string Value)> calls = [];
        int preparations = 0;
        WorkflowFixture fixture = Create((context, _) =>
        {
            Interlocked.Increment(ref preparations);
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"]));
        }, (context, _) =>
        {
            string value = context.Inputs["group"].GetString() + "/" + context.Inputs["value"].GetString();
            calls.Add((context.CausationId, context.ExecutionId, value));
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(value)));
        }, parallel);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        CompiledWorkflow plan = Compile(fixture, Definition(parallel));
        foreach (string group in new[] { "group-20", "group-21" })
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(plan, "receive", Inputs(group, exists)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            Assert.Equal(64, report.Nodes.Single(node => node.NodeId is "loop").Iteration!.Started);
            Assert.Equal(Enumerable.Repeat(group + "/same", 64), Result(report, "end").Outputs["value"].Json.EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(64, calls.Count(call => call.Cause == report.Id));
        }
        Assert.Equal(exists ? 0
                            : 2, preparations);
        Assert.Equal(128, calls.Select(call => call.Execution).Distinct().Count());
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>遅い新規作成の要求と、既存側を通る別要求の値を混ぜません。</summary>
    /// <returns>時刻推測ではなく同期点で確認する準備待ちと要求分離です。</returns>
    [Fact]
    public async Task ConcurrentRequestsKeepTheirPreparationAndValuesAsync()
    {
        TaskCompletionSource preparing = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentBag<string> calls = [];
        WorkflowFixture fixture = Create(async (context, token) =>
        {
            preparing.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NodeResult.Success(context.Inputs["value"]);
        }, (context, _) =>
        {
            string group = context.Inputs["group"].GetString()!;
            calls.Add(group);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(group)));
        }, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        CompiledWorkflow plan = Compile(fixture, Definition(true));
        Task<ExecutionReport> slow = fixture.Engine.ExecuteAsync(plan, "receive", Inputs("slow", false));
        try
        {
            await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.Empty(calls);
            ExecutionReport fast = await fixture.Engine.ExecuteAsync(plan, "receive", Inputs("fast", true)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, fast.Outcome);
            Assert.Equal(64, calls.Count);
            Assert.All(calls, value => Assert.Equal("fast", value));
            Assert.False(slow.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await slow.ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, (await slow.ConfigureAwait(true)).Outcome);
        Assert.Equal(64, calls.Count(value => value is "slow"));
    }

    /// <summary>準備失敗を成功信号に変換せず、後続の副作用を一度も開始しません。</summary>
    /// <returns>不明・失敗の再実行が起きないことの確認です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed)]
    [InlineData(EOutcome.Unknown)]
    public async Task FailedPreparationDoesNotStartBodiesAsync(EOutcome outcome)
    {
        int prepared = 0, bodies = 0;
        WorkflowFixture fixture = Create((_, _) =>
        {
            prepared++;
            return ValueTask.FromResult(NodeResult.Failure("prepare.failure", "Preparation failed", outcome));
        }, (_, _) =>
        {
            bodies++;
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(true)));
        }, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, Definition(false)), "receive", Inputs("failed", false)).ConfigureAwait(true);
        Assert.NotEqual(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(1, prepared);
        Assert.Equal(0, bodies);
    }

    /// <summary>反復の内部にある先着合流を、同じ要素のEndへ収集できます。</summary>
    /// <returns>反復境界を越える別実行を作らないことの確認です。</returns>
    [Fact]
    public async Task FirstInsideLoopPreservesTheElementAsync()
    {
        WorkflowFixture fixture = new(new ForEachNode(), new ForEachEndNode(), new IfNode(), new IfNode(true), new SignalMergeNode("first", new TestStateStore()), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition loop = Node("loop", "control.forEach") with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(conditions) } };
        WorkflowDefinition definition = new("inside", "Inside", [loop, Node("yes", "control.ifStart"), Node("no", "control.ifNotStart"), Node("merge", "control.signal.first"), Node("body", "control.pass"), Node("end", "control.forEach.end")],
            [new("loop", "item", "yes", "condition"), new("loop", "item", "no", "condition"), new("yes", "start", "merge", "input1"), new("no", "start", "merge", "input2"),
                new("loop", "item", "body", "value"), new("merge", "start", "body", "trigger"), new("loop", "loop", "end", "loop"), new("body", "value", "end", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, definition), "loop").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal("[true,false,false,true]", Result(report, "end").Outputs["value"].Json.GetRawText());
    }

    /// <summary>フォルダー準備と音声生成に相当する有限処理だけを置き換えます。</summary>
    /// <returns>実コンパイラーと実ランタイムです。</returns>
    private static WorkflowFixture Create(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> prepare, Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> body, bool parallel)
        => new(new Probe("test.receive", (context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"]))), new Probe("test.prepare", prepare), new Probe("test.body", body),
            new IfNode(), new IfNode(true), new SignalMergeNode("first", new TestStateStore()), new ForEachNode(parallel), new ForEachEndNode());

    /// <summary>Z2と同じ、外側の共通準備完了と内側の要素をQwen相当ノードで待つ配線です。</summary>
    /// <returns>既存配線の最小構成です。</returns>
    private static WorkflowDefinition Definition(bool parallel) => new("scope", "Scoped preparation", [Node("receive", "test.receive"), Node("exists", "control.ifStart"), Node("missing", "control.ifNotStart"),
        Node("prepare", "test.prepare"), Node("prepared", "control.ifStart"), Node("merge", "control.signal.first"), Node("loop", parallel ? "control.parallelForEach"
                                                                                                                                           : "control.forEach"), Node("body", "test.body"), Node("end", "control.forEach.end")],
        [new("receive", "value", "exists", "condition", Path: "/exists"), new("receive", "value", "missing", "condition", Path: "/exists"),
            new("missing", "start", "prepare", "trigger"), new("receive", "value", "prepare", "value", Path: "/group"), new("prepare", CompletionPorts.Success, "prepared", "condition"),
            new("exists", "start", "merge", "input1"), new("prepared", "start", "merge", "input2"), new("receive", CompletionPorts.Signal, "loop", "trigger"), new("receive", "value", "loop", "value", Path: "/items"),
            new("merge", "start", "body", "trigger"), new("loop", "item", "body", "value"), new("receive", "value", "body", "group", Path: "/group"), new("loop", "loop", "end", "loop"), new("body", "value", "end", "value")]);

    /// <summary>同じ要素が64回あっても同じ実行に潰さない入力です。</summary>
    /// <returns>要求ごとに異なるグループ識別子です。</returns>
    private static Dictionary<string, NodeValue> Inputs(string group, bool exists) => new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { group, exists, items = Enumerable.Repeat("same", 64).ToArray() }) };

    /// <summary>コンパイルエラーを実行失敗と混同しません。</summary>
    /// <returns>検証済みの配線です。</returns>
    private static CompiledWorkflow Compile(WorkflowFixture fixture, WorkflowDefinition definition)
    {
        CompilationResult result = fixture.Compiler.Compile(definition);
        Assert.Empty(result.Issues);
        return result.Workflow!;
    }

    /// <summary>対象の結果を一意に読みます。</summary>
    /// <returns>一つの対象の実結果です。</returns>
    private static NodeResult Result(ExecutionReport report, string id) => Assert.Single(report.Nodes.Single(node => string.Equals(node.NodeId, id, StringComparison.Ordinal)).Targets).Result;

    /// <summary>既定設定のノードを置きます。</summary>
    /// <returns>有限ノードです。</returns>
    private static NodeDefinition Node(string id, string type) => new(id, type, JsonSerializer.SerializeToElement(new { }));

    /// <summary>実際の生成やファイル変更を行わず配送回数と文脈を記録します。</summary>
    private sealed class Probe(string type, Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> action) : IActionNode
    {
        /// <summary>実処理と同じ開始・完了の契約です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe(type, type, "Test", "{}", [new("value", EPortValueKind.Json), new("group", EPortValueKind.Json)]) with { HasAsyncCompletion = true };

        /// <summary>試験用の設定を検証します。</summary>
        /// <returns>設定違反なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>同じ配送境界から試験処理へ渡します。</summary>
        /// <returns>記録した実結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => action(context, cancellationToken);
    }
}

