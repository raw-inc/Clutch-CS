using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>共通データが届いても、宛先を生成しなかった分岐を後続で起動しません。</summary>
public sealed class ConditionalInputBranchTests
{
    /// <summary>条件付き定数・型変換・連結を経ても、非選択はデータ入力と設定入力の両方へ伝わります。</summary>
    /// <returns>通常値の到着と分岐の選択が別に扱われる確認です。</returns>
    [Theory]
    [InlineData("control.ifStart", "start", false, false)]
    [InlineData("control.ifStart", "start", true, true)]
    [InlineData("control.ifNotStart", "start", true, false)]
    [InlineData("control.ifNotStart", "start", false, true)]
    [InlineData("control.if", "trueStart", false, false)]
    [InlineData("control.if", "trueStart", true, true)]
    [InlineData("control.if", "falseStart", true, false)]
    [InlineData("control.if", "falseStart", false, true)]
    public async Task BranchFlowsThroughComputedInputsAsync(string gateType, string port, bool condition, bool selected)
    {
        foreach (bool setting in new[] { false, true })
        {
            Sink sink = new(setting);
            WorkflowFixture fixture = Create(sink);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
            ExecutionReport result = await RunAsync(fixture, Definition(setting, condition, gateType, port)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(selected ? 1
                                  : 0, sink.Calls);
            NodeResult received = Result(result, "sink");
            if (selected)
            {
                Assert.Equal(EOutcome.Succeeded, received.Outcome);
                Assert.Equal("127.0.0.9", sink.Destination);
            }
            else
            {
                foreach (string id in new[] { "destination", "convert", "concat", "sink" })
                {
                    Assert.Equal(ExecutionResultReasons.BranchNotSelected, Result(result, id).ReasonCode);
                }
            }
        }
    }

    /// <summary>利用側を明示的に開始した場合は、非選択の宛先を入力不足として報告します。</summary>
    /// <returns>開始要求の未達を成功としない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitStartStillRequiresDestinationAsync(bool setting)
    {
        Sink sink = new(setting);
        WorkflowFixture fixture = Create(sink);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(setting, false);
        definition = definition with { Wires = [.. definition.Wires, new("source", "start", "sink", "trigger")] };
        ExecutionReport result = await RunAsync(fixture, definition).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("input.unavailable", Result(result, "sink").ReasonCode);
        Assert.Equal(0, sink.Calls);
    }

    /// <summary>上流の失敗・結果不明・出力欠損を、正常な非選択へ読み替えません。</summary>
    /// <returns>宛先が確定しなければ副作用を開始しない確認です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, EOutcome.Failed)]
    [InlineData(EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData(EOutcome.Cancelled, EOutcome.Cancelled)]
    [InlineData(EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(EOutcome.Succeeded, EOutcome.Failed)]
    public async Task FailedOrMissingDestinationRemainsAProblemAsync(EOutcome upstream, EOutcome expected)
    {
        Sink sink = new(true);
        WorkflowFixture fixture = Create(sink, new WorkflowFixture.ActionNode((_, _) =>
            ValueTask.FromResult(new NodeResult(upstream, "success", new Dictionary<string, NodeValue>(), "fixture.missing", "No destination"))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(true, true);
        definition = definition with { Nodes = definition.Nodes.Select(node => node.Id switch { "destination" => WorkflowFixture.Definition("destination"), _ => node }).ToArray() };
        ExecutionReport result = await RunAsync(fixture, definition).ConfigureAwait(true);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal("input.unavailable", Result(result, "sink").ReasonCode);
        Assert.Equal(0, sink.Calls);
    }

    /// <summary>不要なデフォルトの分岐が閉じていても、到着済みの通常値を妨げません。</summary>
    /// <returns>nullと非選択が混同されない確認です。</returns>
    [Fact]
    public async Task UnusedFallbackDoesNotCloseTheSelectedValueAsync()
    {
        Sink sink = new(true);
        WorkflowFixture fixture = Create(sink, new DefaultIfNullNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(true, false);
        definition = definition with
        {
            Nodes = [.. definition.Nodes, WorkflowFixture.Definition("fallback", "data.defaultIfNull")],
            Wires = [.. definition.Wires.Select(wire => wire.FromNode switch { "concat" => wire with { ToNode = "fallback", ToPort = "defaultValue" }, _ => wire }),
                new("source", "value", "fallback", "value"), new("fallback", "value", "sink", "$settings.remoteAddress")],
        };
        ExecutionReport result = await RunAsync(fixture, definition).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, sink.Calls);
        Assert.Equal("packet", sink.Destination);
    }

    /// <summary>成功結果のJSON nullは通常の値として扱い、既定値選択を実行できます。</summary>
    /// <returns>分岐を閉じる代わりに、nullを既定値へ変換する確認です。</returns>
    [Fact]
    public async Task ExplicitNullStillSelectsTheDefaultAsync()
    {
        Sink sink = new(true);
        WorkflowFixture fixture = Create(sink, new DefaultIfNullNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("null-input", "Null input", [WorkflowFixture.Definition("source", "control.button"),
            WorkflowFixture.Definition("fallback", "data.defaultIfNull") with { Inputs = new Dictionary<string, NodeValue> { ["defaultValue"] = NodeValue.FromObject("127.0.0.2") } },
            WorkflowFixture.Definition("sink", "test.branchSink")],
            [new("source", "value", "fallback", "value"), new("source", "value", "sink", "value"), new("fallback", "value", "sink", "$settings.remoteAddress")]);
        ExecutionReport result = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "source",
            new Dictionary<string, NodeValue> { ["value"] = NodeValue.Null }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, sink.Calls);
        Assert.Equal("127.0.0.2", sink.Destination);
    }

    /// <summary>成功ノードの明示経路が選ばれなかった場合も、下流の設定生成へ理由を伝えます。</summary>
    /// <returns>条件ノード固有の出力名だけに依存しない非選択です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnselectedResultRouteClosesDependentInputsAsync(bool selected)
    {
        Sink sink = new(true);
        string route = "false";
        if (selected)
        {
            route = "true";
        }
        WorkflowFixture fixture = Create(sink, new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(
            new NodeResult(EOutcome.Succeeded, route, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(true) }))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(true, true);
        definition = definition with
        {
            Nodes = definition.Nodes.Select(node => node.Id switch { "gate" => WorkflowFixture.Definition("gate"), _ => node }).ToArray(),
            Wires = definition.Wires.Select(wire => wire.FromNode switch { "gate" => wire with { FromPort = "value", When = "true" }, _ => wire }).ToArray(),
        };
        ExecutionReport result = await RunAsync(fixture, definition).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(selected ? 1
                              : 0, sink.Calls);
        if (!selected)
        {
            Assert.Equal(ExecutionResultReasons.BranchNotSelected, Result(result, "sink").ReasonCode);
        }
    }

    /// <summary>副作用を観測する処理だけを試験用に置き換えます。</summary>
    /// <returns>純粋値と条件判定は実装そのものを使う実行環境です。</returns>
    internal static WorkflowFixture Create(INode sink, params INode[] extra)
        => new([new ButtonNode(), new IfNode(), new IfNode(true), new IfElseNode(), new ConstantValueNode(), new ConvertNode(), new ConcatNode(), sink, .. extra]);

    /// <summary>個別宛先は条件付きで計算し、本文は同じ要求から直接渡します。</summary>
    /// <returns>OSC・HTTPなどの動的宛先と同じ依存関係です。</returns>
    internal static WorkflowDefinition Definition(bool setting, bool condition, string gateType = "control.ifStart", string port = "start")
    {
        string destination = "destination";
        if (setting)
        {
            destination = "$settings.remoteAddress";
        }
        return new("conditional-input", "Conditional input", [WorkflowFixture.Definition("source", "control.button"),
            WorkflowFixture.Definition("gate", gateType) with { Inputs = new Dictionary<string, NodeValue> { ["condition"] = NodeValue.FromObject(condition) } },
            WorkflowFixture.Definition("destination", "value.constant", """{"type":"integer","text":"9"}"""),
            WorkflowFixture.Definition("convert", "data.convert", """{"targetType":"string"}"""),
            WorkflowFixture.Definition("concat", "data.concat") with { Inputs = new Dictionary<string, NodeValue> { ["input1"] = NodeValue.FromObject("127.0.0.") } },
            WorkflowFixture.Definition("sink", "test.branchSink")],
            [new("source", "start", "gate", "trigger"), new("gate", port, "destination", "trigger"), new("destination", "value", "convert", "value"),
                new("convert", "value", "concat", "input2"), new("concat", "value", "sink", destination), new("source", "value", "sink", "value")]);
    }

    /// <summary>一つの開始要求を、外部通信なしで実行します。</summary>
    /// <returns>実ランタイムが判定したノードとWFの結果です。</returns>
    internal static async Task<ExecutionReport> RunAsync(WorkflowFixture fixture, WorkflowDefinition definition)
    {
        Runtime.Workflows.Compilation.CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        return await fixture.Engine.ExecuteAsync(compiled.Workflow!, "source", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("packet") }).ConfigureAwait(true);
    }

    /// <summary>期待する一対象の結果を取得します。</summary>
    /// <returns>副作用の成否と理由です。</returns>
    internal static NodeResult Result(ExecutionReport report, string id)
        => Assert.Single(Assert.Single(report.Nodes, node => string.Equals(node.NodeId, id, StringComparison.Ordinal)).Targets).Result;

    /// <summary>必要な宛先と本文を受け取り、通信を行わず実行回数だけを記録します。</summary>
    internal sealed class Sink : IActionNode
    {
        private readonly bool setting;
        private int calls;

        /// <summary>通常入力と動的設定の双方を、同じ実行経路で検証します。</summary>
        internal Sink(bool setting)
        {
            this.setting = setting;
            Descriptor = NodeSupport.Describe("test.branchSink", "Sink", "Test", "{}", [new("value", Required: true), new("destination", Required: true)]);
            if (setting)
            {
                Descriptor = Descriptor with { Inputs = [new("value", Required: true)], SettingsSchema = Runtime.Configuration.NodeSettings.Schema(
                    """{"type":"object","required":["remoteAddress"],"properties":{"remoteAddress":{"type":"string"}}}""") };
            }
        }

        /// <summary>観測した実行回数です。</summary>
        internal int Calls => Volatile.Read(ref calls);

        /// <summary>実行時に確定した宛先です。</summary>
        internal string? Destination
        {
            get; private set;
        }

        /// <summary>試験対象の必須入力契約です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        }

        /// <summary>設定の不足と型を通常どおり検証します。</summary>
        /// <returns>設定上の問題です。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

        /// <summary>本番へ送信せず、確定した宛先を記録します。</summary>
        /// <returns>試験対象の処理結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            Destination = setting ? context.Settings.GetProperty("remoteAddress").GetString()
                                  : context.Inputs["destination"].GetString();
            return ValueTask.FromResult(NodeResult.Success());
        }
    }
}
