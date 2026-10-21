using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>未受信の本番値とダミー値を切り替え、未選択の入力を必須扱いしません。</summary>
public sealed class ToggleButtonInputTests
{
    /// <summary>どちらの候補も選択でき、別の反復領域があっても未受信の枝を起動しません。</summary>
    /// <returns>選択値の供給、未供給時の失敗、手動切替と参照の分離です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MissingUnselectedInputDoesNotBlockReadsOrChanges(bool selectedOn, bool iteration)
    {
        TestStateStore store = new();
        int calls = 0;
        WorkflowFixture fixture = new(new ButtonNode(), new ToggleButtonNode(store), new ConstantValueNode(), new CommentNode(), new PassNode(), new ForEachNode(),
            new WorkflowFixture.ActionNode((_, _) =>
            {
                calls++;
                return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject("unexpected")));
            }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = store;
        WorkflowDefinition definition = Definition(selectedOn);
        if (iteration)
        {
            definition = definition with { Nodes = [.. definition.Nodes, WorkflowFixture.Definition("loop", "control.forEach") with
            {
                Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(Array.Empty<int>()) },
            }] };
        }
        _ = await store.CompareExchangeAsync(NodeStateScope.Shared(definition.Id), ToggleButtonState.Key("toggle"), null,
            ContractJson.ToValue(new ToggleButtonState(selectedOn, []))).ConfigureAwait(true);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport first = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, first.Outcome);
        Assert.Equal("dummy", Output(first, "consumer", "value").GetString());
        Assert.Equal(EOutcome.NotExecuted, first.Nodes.Single(node => node.NodeId is "changed").Outcome);
        Assert.Equal(0, calls);

        // 未受信側へも状態は切り替えられますが、接続済みの未供給値をnullとして流しません。
        ExecutionReport unavailable = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "toggle", Trigger()).ConfigureAwait(true);
        Assert.Equal(!selectedOn, Output(unavailable, "toggle", "isOn").Json.GetBoolean());
        Assert.True(Output(unavailable, "toggle", "start").Json.GetBoolean());
        Assert.Equal(EOutcome.Failed, unavailable.Outcome);
        Assert.DoesNotContain(unavailable.Nodes.Single(node => node.NodeId is "toggle").Targets, target => target.Result.Outputs.ContainsKey("value"));
        ExecutionReport read = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, read.Outcome);
        Assert.Contains(read.Nodes.Single(node => node.NodeId is "consumer").Targets, target => target.Result.ReasonCode is "input.unavailable");

        ExecutionReport restored = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "toggle", Trigger()).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, restored.Outcome);
        Assert.Equal("dummy", Output(restored, "changed", "value").GetString());
        Assert.Equal(selectedOn, Output(restored, "toggle", "isOn").Json.GetBoolean());
        Assert.Equal(0, calls);
        VersionedState? saved = await store.ReadAsync(NodeStateScope.Shared(definition.Id), ToggleButtonState.Key("toggle")).ConfigureAwait(true);
        Assert.True(ToggleButtonState.TryRead(saved!.Value, out ToggleButtonState state));
        Assert.Equal(selectedOn, state.IsOn);
        Assert.Equal(2, state.Events.Count);
    }

    /// <summary>未配線の任意入力と、明示的なnull値は引き続き正常なnullを返します。</summary>
    /// <returns>未供給の配線とは区別した正常値です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnwiredAndExplicitNullRemainValues(bool explicitNull)
    {
        WorkflowFixture fixture = new(new ButtonNode(), new ToggleButtonNode(new TestStateStore()), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition toggle = WorkflowFixture.Definition("toggle", "control.toggleButton");
        if (explicitNull)
        {
            toggle = toggle with { Inputs = new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.Null } };
        }
        WorkflowDefinition definition = new("null-toggle", "Null toggle",
            [WorkflowFixture.Definition("button", "control.button"), toggle, WorkflowFixture.Definition("consumer", "control.pass")],
            [new("button", "start", "consumer", "trigger"), new("toggle", "value", "consumer", "value")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!, "button").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Output(report, "consumer", "value").Json.ValueKind);
    }

    /// <summary>未起動の実処理と、コメント越しのダミー値を候補へ配線します。</summary>
    /// <returns>実際の音声生成と同じ入力不足を持つ最小グラフです。</returns>
    private static WorkflowDefinition Definition(bool selectedOn)
    {
        string available = selectedOn ? "onValue"
                                      : "offValue";
        string missing = selectedOn ? "offValue"
                                    : "onValue";
        return new("toggle-input-test", "Toggle inputs",
            [WorkflowFixture.Definition("button", "control.button"), WorkflowFixture.Definition("missing"),
             WorkflowFixture.Definition("comment", "value.comment"), WorkflowFixture.Definition("dummy", "value.constant", """{"text":"dummy"}"""),
             WorkflowFixture.Definition("toggle", "control.toggleButton"), WorkflowFixture.Definition("consumer", "control.pass"), WorkflowFixture.Definition("changed", "control.pass")],
            [new("missing", "value", "comment", "value"), new("comment", "value", "toggle", missing), new("dummy", "value", "toggle", available),
             new("button", "start", "consumer", "trigger"), new("toggle", "value", "consumer", "value"), new("toggle", "start", "changed", "trigger"), new("toggle", "value", "changed", "value")]);
    }

    /// <summary>実際に出力された値だけを取り出します。</summary>
    /// <returns>単一実行先の出力です。</returns>
    private static NodeValue Output(ExecutionReport report, string node, string port)
        => Assert.Single(report.Nodes.Single(result => string.Equals(result.NodeId, node, StringComparison.Ordinal)).Targets).Result.Outputs[port];

    /// <summary>手動操作で一回だけ状態を反転します。</summary>
    /// <returns>押下信号です。</returns>
    private static Dictionary<string, NodeValue> Trigger() => new() { ["trigger"] = NodeValue.FromObject(true) };
}
