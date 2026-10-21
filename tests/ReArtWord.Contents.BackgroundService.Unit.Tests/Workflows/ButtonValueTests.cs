using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>ボタンの複数値を一つの押下として配送し、型と実行ごとの組を保持します。</summary>
public sealed class ButtonValueTests
{
    /// <summary>異なる型と正常なnullを、余分な未使用出力なしに後続へ渡します。</summary>
    /// <returns>実コンパイラー・エンジン・型推論を通した結果です。</returns>
    [Fact]
    public async Task PressDeliversAllValuesWithOneSignalAsync()
    {
        Dictionary<string, NodeValue> values = new(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject("text"),
            ["value2"] = NodeValue.FromObject(42),
            ["value3"] = NodeValue.FromObject(false),
            ["value4"] = NodeValue.FromObject(new { number = 7 }),
            ["value5"] = NodeValue.FromBytes(new byte[] { 0, 128, 255 }),
            ["value6"] = NodeValue.Null,
            ["value7"] = NodeValue.FromObject("C:/data") with { DataType = new("path") },
        };
        List<NodeExecutionContext> received = [];
        WorkflowFixture fixture = new(new ButtonNode(), new Probe((context, _) =>
        {
            received.Add(context);
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(values, values.Keys);
        CompiledWorkflow plan = Compile(fixture, definition);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(plan, "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        NodeExecutionContext call = Assert.Single(received);
        Assert.True(call.Inputs["trigger"].Json.GetBoolean());
        NodeResult result = Assert.Single(report.Nodes.Single(node => node.NodeId is "button").Targets).Result;
        Assert.Equal(values.Count + 1, result.Outputs.Count);
        WorkflowTypeInference types = new(definition, fixture.Registry.Descriptors);
        foreach ((string port, NodeValue expected) in values)
        {
            Assert.Equal(ContractJson.Hash(expected), ContractJson.Hash(call.Inputs[port]));
            Assert.Equal(types.PortType("button", port, false), types.PortType("button", port, true));
        }
        Assert.Equal("integer", types.PortType("button", "value4/number", true)!.Type);
    }

    /// <summary>配線の値を押下時に解決し、出力だけ接続した空欄はnullとして渡します。</summary>
    /// <returns>途中の欠番、プロパティ出力、最大番号と元の保存値の保持です。</returns>
    [Fact]
    public async Task ResolvesConnectionsAndKeepsUnspecifiedConnectedOutputsAsync()
    {
        NodeExecutionContext? received = null;
        WorkflowFixture fixture = new(new ButtonNode(), new ConstantValueNode(), new Probe((context, _) =>
        {
            received = context;
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(new Dictionary<string, NodeValue> { ["label"] = NodeValue.FromObject("Go") }, ["value", "value3", "value64"]);
        NodeDefinition constant = WorkflowFixture.Definition("constant", "value.constant", """{"type":"auto","text":"{\"number\":9}"}""");
        definition = definition with { Nodes = [constant, .. definition.Nodes], Wires = [new("constant", "value", "button", "value3"), new("button", "value3/number", "sink", "value2"), .. definition.Wires] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, definition), "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.NotNull(received);
        Assert.Equal(JsonValueKind.Null, received.Inputs["value"].Json.ValueKind);
        Assert.Equal(JsonValueKind.Null, received.Inputs["value64"].Json.ValueKind);
        Assert.Equal(9, received.Inputs["value2"].Json.GetInt32());
        Assert.Equal(9, received.Inputs["value3"].Json.GetProperty("number").GetInt32());
        NodeResult result = Assert.Single(report.Nodes.Single(node => node.NodeId is "button").Targets).Result;
        Assert.Equal(4, result.Outputs.Count);
        Assert.False(result.Outputs.ContainsKey("value2"));
        Assert.Equal("Go", definition.Nodes[1].Inputs!["label"].GetString());
        Assert.Single(definition.Nodes[1].Inputs!);
    }

    /// <summary>後続の待機中に別の押下が完了しても、複数値の組を混ぜません。</summary>
    /// <returns>同じ保存版の二実行と保持値を共有しても独立した入力です。</returns>
    [Fact]
    public async Task OverlappingPressesPreserveTheirOwnValueSetsAsync()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<(string Name, int Number)> received = new();
        string firstName = "A", valuePort = "value";
        WorkflowFixture fixture = new(new ButtonNode(), new Probe(async (context, token) =>
        {
            if (string.Equals(context.Inputs[valuePort].GetString(), firstName, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            received.Enqueue((context.Inputs["value"].GetString()!, context.Inputs["value2"].Json.GetInt32()));
            return NodeResult.Success();
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = new TestStateStore();
        CompiledWorkflow plan = Compile(fixture, Definition(null, ["value", "value2"]));
        Task<ExecutionReport> first = fixture.Engine.ExecuteAsync(plan, "button", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("A"), ["value2"] = NodeValue.FromObject(11) });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            ExecutionReport second = await fixture.Engine.ExecuteAsync(plan, "button", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("B"), ["value2"] = NodeValue.FromObject(22) }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, second.Outcome);
            Assert.False(first.IsCompleted);
            Assert.Equal(("B", 22), Assert.Single(received));
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        }
        Assert.Equal(new[] { ("B", 22), ("A", 11) }, received);
    }

    /// <summary>契約上の上限と別名を実コンパイラーでも検証します。</summary>
    /// <returns>最大64組は有効、別名と範囲外は無効です。</returns>
    [Theory]
    [InlineData("value64", true)]
    [InlineData("value1", false)]
    [InlineData("value02", false)]
    [InlineData("value65", false)]
    public async Task ValidatesPortBoundariesAsync(string port, bool valid)
    {
        WorkflowFixture fixture = new(new ButtonNode(), new Probe((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompilationResult compiled = fixture.Compiler.Compile(Definition(new Dictionary<string, NodeValue> { [port] = NodeValue.FromObject(1) }, [port]));
        Assert.Equal(valid, compiled.Issues.Count == 0);
    }

    /// <summary>同じ後続へ複数の値と一件の開始信号を接続します。</summary>
    /// <returns>押下でだけ開始する保存定義です。</returns>
    private static WorkflowDefinition Definition(IReadOnlyDictionary<string, NodeValue>? values, IEnumerable<string> ports) => new("button-values", "Button values",
        [WorkflowFixture.Definition("button", ButtonValuePorts.NodeType) with { Inputs = values }, WorkflowFixture.Definition("sink", "test.buttonValues")],
        [new("button", "start", "sink", "trigger"), .. ports.Select(port => new WireDefinition("button", port, "sink", port))]);

    /// <summary>通常の保存時検証を省略しません。</summary>
    /// <returns>実行可能な保存版です。</returns>
    private static CompiledWorkflow Compile(WorkflowFixture fixture, WorkflowDefinition definition)
    {
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        return compiled.Workflow!;
    }

    /// <summary>同一後続が受け取った複数値を実行の外から観測します。</summary>
    private sealed class Probe(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> action) : IActionNode
    {
        /// <summary>それぞれの値を独立した入力で受け取ります。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.buttonValues", "Button values", "Test", "{}", Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ButtonValuePorts.Name(index))).ToArray());

        /// <summary>観測ノードには設定はありません。</summary>
        /// <returns>問題なしです。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>本番の入力解決・配送から受け取った値を試験します。</summary>
        /// <returns>観測処理の結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default) => action(context, cancellationToken);
    }
}
