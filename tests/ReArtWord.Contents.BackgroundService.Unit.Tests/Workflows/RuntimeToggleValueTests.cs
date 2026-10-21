using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Scheduling;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>Toggleの選択値を生成する実行時ノードを、要求単位で一度だけ評価します。</summary>
public sealed class RuntimeToggleValueTests
{
    private static readonly int[] elements = [1, 2, 3];
    private static readonly int[] candidates = [7];
    private const string RuntimeId = "runtime";
    private const string TimeNode = "scheduling.now";

    /// <summary>日時・乱数・抽選をループから参照でき、別の要求では新しく評価します。</summary>
    /// <returns>ON/OFF両側、逐次・並列反復と静的評価の分離です。</returns>
    [Theory]
    [InlineData("scheduling.now", false, false)]
    [InlineData("scheduling.now", true, false)]
    [InlineData("scheduling.now", false, true)]
    [InlineData("scheduling.now", true, true)]
    [InlineData("math.random", false, false)]
    [InlineData("math.random", true, false)]
    [InlineData("math.random", false, true)]
    [InlineData("math.random", true, true)]
    [InlineData("data.randomPick", false, false)]
    [InlineData("data.randomPick", true, false)]
    [InlineData("data.randomPick", false, true)]
    [InlineData("data.randomPick", true, true)]
    public async Task RuntimeValuesAreSharedPerRequestAcrossIterationAsync(string type, bool selectedOn, bool parallel)
    {
        TestStateStore store = new();
        WorkflowFixture fixture = new(new ButtonNode(), new ToggleButtonNode(store), new PassNode(), new ForEachNode(parallel), new ForEachEndNode(),
            new CurrentTimeNode(), new RandomNode(), new RandomPickNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = store;
        FixedClock clock = new(DateTimeOffset.Parse("2026-10-07T00:00:00Z", CultureInfo.InvariantCulture));
        NodeExecutor executor = new(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, timeProvider: clock);
        int calls = 0;
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, RuntimeId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref calls);
            }
            return executor.ExecuteAsync(request, token);
        };
        string loopType = parallel ? "control.parallelForEach"
                                   : "control.forEach";
        WorkflowDefinition definition = Graph(Runtime(type), selectedOn);
        definition = definition with
        {
            Nodes = [.. definition.Nodes, WorkflowFixture.Definition("loop", loopType) with
            {
                Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(elements) },
            }, WorkflowFixture.Definition("end", "control.forEach.end")],
            Wires = [new("button", "start", "loop", "trigger"), new("loop", "start", "consumer", "trigger"),
                new("runtime", "value", "toggle", SelectedPort(selectedOn)), new("toggle", "value", "consumer", "value"),
                new("loop", "loop", "end", "loop"), new("consumer", "value", "end", "value")],
        };
        await store.CompareExchangeAsync(NodeStateScope.Shared(definition.Id), ToggleButtonState.Key("toggle"), null,
            ContractJson.ToValue(new ToggleButtonState(selectedOn, []))).ConfigureAwait(true);
        CompiledWorkflow compiled = IterationTests.Compile(fixture, definition);
        Assert.Equal(0, calls);
        Assert.False(fixture.Registry.Descriptors.Single(node => string.Equals(node.Type, type, StringComparison.Ordinal)).CanEvaluateStatically);
        for (int run = 1; run <= 2; run++)
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled, "button").ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            Assert.Equal(run, calls);
            JsonElement output = Output(report, "end").Json;
            Assert.Equal(3, output.GetArrayLength());
            string expected = string.Equals(type, TimeNode, StringComparison.Ordinal) ? "2026-10-07"
                                                                                      : "7";
            Assert.All(output.EnumerateArray(), item => Assert.Equal(expected, item.ToString()));
            Assert.Equal(selectedOn, Assert.Single(report.Nodes.Single(node => node.NodeId is "toggle").Targets).Result.Outputs["isOn"].Json.GetBoolean());
        }
    }

    /// <summary>実行時の数値が変換ノードを経由しても、選択値を遡れます。</summary>
    /// <returns>表示分類だけに依存しない実際の変換経路です。</returns>
    [Fact]
    public async Task ConversionBetweenRuntimeValueAndToggleIsEvaluatedAsync()
    {
        WorkflowFixture fixture = new(new ButtonNode(), new ToggleButtonNode(new TestStateStore()), new RandomNode(), new ConvertNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition graph = Graph(Runtime("math.random"), false);
        graph = graph with
        {
            Nodes = [.. graph.Nodes, WorkflowFixture.Definition("convert", "data.convert", """{"targetType":"string"}""")],
            Wires = [new("button", "start", "consumer", "trigger"), new("runtime", "value", "convert", "value"),
                new("convert", "value", "toggle", "offValue"), new("toggle", "value", "consumer", "value")],
        };
        ExecutionReport result = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, graph), "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("7", Output(result, "consumer").GetString());
    }

    /// <summary>値ノードでも明示された開始信号を迂回せず、発火済み結果を再利用します。</summary>
    /// <returns>未発火時の未実行と、発火後の保持結果です。</returns>
    [Fact]
    public async Task RuntimeValueRetainsItsExplicitStartBoundaryAsync()
    {
        TestStateStore store = new();
        WorkflowFixture fixture = new(new ButtonNode(), new ToggleButtonNode(store), new RandomNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = store;
        int calls = 0;
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, RuntimeId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref calls);
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        WorkflowDefinition graph = Graph(Runtime("math.random"), false);
        graph = graph with { Nodes = [.. graph.Nodes, WorkflowFixture.Definition("update", "control.button")], Wires = [.. graph.Wires, new("update", "start", "runtime", "trigger")] };
        CompiledWorkflow compiled = IterationTests.Compile(fixture, graph);
        ExecutionReport before = await fixture.Engine.ExecuteAsync(compiled, "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, before.Outcome);
        Assert.Equal(0, calls);
        await fixture.Engine.ExecuteAsync(compiled, "update").ConfigureAwait(true);
        ExecutionReport after = await fixture.Engine.ExecuteAsync(compiled, "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, after.Outcome);
        Assert.Equal(7, Output(after, "consumer").Json.GetInt32());
        Assert.Equal(1, calls);
    }

    /// <summary>値という表示分類だけでは、一般の処理を候補参照から開始しません。</summary>
    /// <returns>選択・未選択のどちらでも外部処理を暗黙開始しない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisplayClassificationDoesNotAuthorizeDemandExecutionAsync(bool chooseAction)
    {
        TestStateStore store = new();
        DisplayValueNode action = new();
        WorkflowFixture fixture = new(new ButtonNode(), new ToggleButtonNode(store), new PassNode(), action);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition graph = Graph(WorkflowFixture.Definition(RuntimeId, action.Descriptor.Type), false);
        graph = graph with { Nodes = [.. graph.Nodes.Where(node => node.Id is not "toggle"), WorkflowFixture.Definition("toggle", "control.toggleButton") with
        {
            Inputs = new Dictionary<string, NodeValue> { ["onValue"] = NodeValue.FromObject("dummy") },
        }] };
        await store.CompareExchangeAsync(NodeStateScope.Shared(graph.Id), ToggleButtonState.Key("toggle"), null,
            ContractJson.ToValue(new ToggleButtonState(!chooseAction, []))).ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, graph), "button").ConfigureAwait(true);
        EOutcome expected = chooseAction ? EOutcome.Failed
                                         : EOutcome.Succeeded;
        Assert.Equal(expected, report.Outcome);
        Assert.Equal(0, action.Calls);
        if (!chooseAction)
        {
            Assert.Equal("dummy", Output(report, "consumer").GetString());
        }
    }

    /// <summary>選択値を別の開始事象から読み、Toggle自身へ切替信号を送りません。</summary>
    /// <returns>本番QR経路の値依存を抜き出した最小グラフです。</returns>
    private static WorkflowDefinition Graph(NodeDefinition runtime, bool selectedOn) => new("runtime-toggle", "Runtime toggle", [
        WorkflowFixture.Definition("button", "control.button"), runtime, WorkflowFixture.Definition("toggle", "control.toggleButton"), WorkflowFixture.Definition("consumer", "control.pass")],
        [new("button", "start", "consumer", "trigger"), new("runtime", "value", "toggle", SelectedPort(selectedOn)), new("toggle", "value", "consumer", "value")]);

    /// <summary>実行しない静的検証と、一回の実行が必要な値を区別します。</summary>
    /// <returns>試験する実組込みノードです。</returns>
    private static NodeDefinition Runtime(string type) => type switch
    {
        "scheduling.now" => WorkflowFixture.Definition("runtime", type, """{"timeZoneId":"Asia/Tokyo","format":"yyyy-MM-dd"}"""),
        "math.random" => WorkflowFixture.Definition("runtime", type, """{"mode":"integer","minimum":7,"maximum":7}"""),
        _ => WorkflowFixture.Definition("runtime", type) with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(candidates) } },
    };

    /// <summary>保存されたON/OFFに対応する入力名です。</summary>
    /// <returns>選択する候補入力です。</returns>
    private static string SelectedPort(bool selectedOn) => selectedOn ? "onValue"
                                                                      : "offValue";

    /// <summary>一台で実際に返された出力を読みます。</summary>
    /// <returns>ノードのvalue出力です。</returns>
    private static NodeValue Output(ExecutionReport report, string id)
        => Assert.Single(report.Nodes.Single(node => string.Equals(node.NodeId, id, StringComparison.Ordinal)).Targets).Result.Outputs["value"];

    /// <summary>表示を値に分類しても、要求時評価の契約を持たない通常の処理です。</summary>
    private sealed class DisplayValueNode : IActionNode
    {
        /// <summary>表示用の分類だけを宣言します。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.displayValue", "Display value", "Test", "{}", inputs: []) with { IsValueOperation = true };

        /// <summary>誤って開始されていないかを数えます。</summary>
        internal int Calls
        {
            get; private set;
        }

        /// <summary>設定は試験で固定しています。</summary>
        /// <returns>追加の検証問題はありません。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>通常の処理が開始された場合だけ記録します。</summary>
        /// <returns>呼出しを検出する値です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("unexpected")));
        }
    }
}
