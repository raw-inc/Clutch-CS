using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>SubWorkflow定義上の操作と、複数の呼出し先から参照する状態を一致させます。</summary>
public sealed class SubworkflowButtonTests
{
    /// <summary>定義上で切り替えた値を複数配置から参照し、参照自体では反転しません。</summary>
    /// <returns>入れ子を含む定義の操作・展開・状態共有の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefinitionToggleIsSharedWithPlacementsWithoutRepeatingItsSignalAsync(bool nested)
    {
        TestStateStore store = new();
        WorkflowFixture fixture = new(new ButtonNode(), new ToggleButtonNode(store), new SubworkflowBoundaryNode(false), new SubworkflowBoundaryNode(true));
        fixture.Registry.Dynamic = new SubworkflowCatalog(fixture.Registry, fixture.Sources.GetEffectiveWorkflows);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition toggle = WorkflowFixture.Definition("toggle", "control.toggleButton") with
        {
            Inputs = new Dictionary<string, NodeValue> { ["offValue"] = NodeValue.FromObject("OFF"), ["onValue"] = NodeValue.FromObject("ON") },
        };
        WorkflowDefinition child = new("child", "Child", [toggle, SubworkflowTests.Field("out", true, "selected")], [new("toggle", "value", "out", "value")]);
        CompiledWorkflow childPlan = SubworkflowTests.Compile(fixture, child);
        await fixture.Sources.ApplyAsync(childPlan);
        string target = child.Id;
        if (nested)
        {
            WorkflowDefinition middle = new("middle", "Middle", [Call("inner", target), SubworkflowTests.Field("out", true, "selected")],
                [new("inner", "selected", "out", "value")]);
            await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, middle));
            target = middle.Id;
        }
        WorkflowDefinition parent = new("parent", "Parent", [Call("one", target), Call("two", target)], []);
        CompiledWorkflow parentPlan = SubworkflowTests.Compile(fixture, parent);
        ExecutionReport before = await fixture.Engine.ExecuteAsync(parentPlan, RecipeNodeIdentity.Child("one", "out"));
        Assert.True(before.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(before));
        Assert.Equal("OFF", SubworkflowTests.Output(before, "one", "out").GetString());
        ExecutionReport pressed = await fixture.Engine.ExecuteAsync(childPlan, "toggle", new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) });
        Assert.Equal(EOutcome.Succeeded, pressed.Outcome);
        ExecutionReport after = await fixture.Engine.ExecuteAsync(parentPlan, RecipeNodeIdentity.Child("one", "out"));
        Assert.Equal(EOutcome.Succeeded, after.Outcome);
        Assert.Equal("ON", SubworkflowTests.Output(after, "one", "out").GetString());
        ExecutionReport second = await fixture.Engine.ExecuteAsync(parentPlan, RecipeNodeIdentity.Child("two", "out"));
        Assert.Equal("ON", SubworkflowTests.Output(second, "two", "out").GetString());
        Assert.True(ToggleButtonState.TryRead((await store.ReadAsync(NodeStateScope.Shared(child.Id), ToggleButtonState.Key(toggle.Id)))!.Value, out ToggleButtonState state));
        Assert.True(state.IsOn);
        Assert.Single(state.Events);
    }

    /// <summary>信号付きボタンをSubWorkflow定義から開始でき、後続へ値を渡します。</summary>
    /// <returns>トグル以外のボタン操作も同じ受付経路で動作することの確認です。</returns>
    [Fact]
    public async Task OrdinaryButtonCanStartInsideRecipeDefinitionAsync()
    {
        WorkflowFixture fixture = new(new ButtonNode(), new SubworkflowBoundaryNode(true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition button = WorkflowFixture.Definition("button", "control.button") with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("pressed") } };
        WorkflowDefinition child = new("child", "Child", [button, SubworkflowTests.Field("out", true, "value")], [new("button", "value", "out", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(SubworkflowTests.Compile(fixture, child), button.Id);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Contains(report.Nodes, node => string.Equals(node.NodeId, button.Id, StringComparison.Ordinal) && node.Targets.Any(target => target.Result.Outputs.ContainsKey("start")));
    }

    /// <summary>有効化済み定義を参照する配置です。</summary>
    /// <returns>実際のコンパイラーで展開する呼出しです。</returns>
    private static NodeDefinition Call(string id, string definition) => new(id, SubworkflowInterface.CallType(definition), JsonSerializer.SerializeToElement(new { }));

    /// <summary>定義側で明示操作するまでは旧配置の選択を保持し、明示操作後は共有状態を優先します。</summary>
    /// <returns>更新直後の暗黙な切替と、同じ押下の二重反転を避ける確認です。</returns>
    [Fact]
    public async Task LegacyPlacementSelectionSurvivesUntilDefinitionIsExplicitlyChangedAsync()
    {
        TestStateStore store = new();
        string placed = RecipeNodeIdentity.Child("call", "toggle");
        await store.CompareExchangeAsync(NodeStateScope.Shared("parent"), ToggleButtonState.Key(placed), null, ContractJson.ToValue(new ToggleButtonState(true, [])));
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), "parent", "revision", placed, "p",
            ToggleButtonSource.Capture(JsonSerializer.SerializeToElement(new { }), "child", "toggle"), new Dictionary<string, NodeValue>(), TimeProvider.System)
        { IsValueRead = true, ContractVersion = 2 };
        ToggleButtonNode node = new(store);
        Assert.True((await node.ExecuteAsync(context)).Outputs["isOn"].Json.GetBoolean());
        Assert.Null(await store.ReadAsync(NodeStateScope.Shared("child"), ToggleButtonState.Key("toggle")));
        NodeExecutionContext press = context with { WorkflowId = "child", NodeId = "toggle", Settings = JsonSerializer.SerializeToElement(new { }), IsValueRead = false,
            Inputs = new Dictionary<string, NodeValue> { ["trigger"] = NodeValue.FromObject(true) } };
        Assert.True((await node.ExecuteAsync(press)).Outputs["isOn"].Json.GetBoolean());
        Assert.True((await node.ExecuteAsync(press)).Outputs["isOn"].Json.GetBoolean());
        Assert.False((await node.ExecuteAsync(press with { CausationId = Guid.NewGuid() })).Outputs["isOn"].Json.GetBoolean());
        Assert.False((await node.ExecuteAsync(context)).Outputs["isOn"].Json.GetBoolean());
    }
}
