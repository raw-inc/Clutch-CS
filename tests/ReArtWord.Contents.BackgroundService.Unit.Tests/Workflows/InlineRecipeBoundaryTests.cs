using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>レシピ境界で実行値と経路を変えないことを確認します。</summary>
public sealed class InlineRecipeBoundaryTests
{
    /// <summary>JSON以外の値と専用PC値を、内部へ渡して親の後続へ返せます。</summary>
    /// <returns>通常グラフと同じ型の値です。</returns>
    [Theory]
    [InlineData("binary")]
    [InlineData("pc-selection")]
    public async Task SpecialValuesSurviveInputAndOutputBoundariesAsync(string type)
    {
        WorkflowFixture fixture = Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeValue value = NodeValue.FromBytes(new byte[] { 0, 1, 255 });
        string pcType = PcSelectionPorts.TypeName;
        if (string.Equals(type, pcType, StringComparison.Ordinal))
        {
            value = ContractJson.ToValue(new[] { new PcSelection(AllHosts: true) });
        }
        WorkflowDefinition recipe = new("recipe", "Recipe", [Field("in", false, "data", type), Field("out", true, "data", type)], [new("in", "value", "out", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        NodeDefinition call = new("group", SubworkflowInterface.CallType(recipe.Id), JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["data"] = value });
        if (string.Equals(type, pcType, StringComparison.Ordinal))
        {
            NodeDefinition sink = new("sink", "control.pass", JsonSerializer.SerializeToElement(new { }));
            CompiledWorkflow plan = SubworkflowTests.Compile(fixture, new("parent", "Parent", [call, sink], [new(call.Id, "data", sink.Id, PcSelectionPorts.Execution)]));
            Assert.True(Assert.Single(plan.PcSelections![sink.Id]).AllHosts);
            NodeValue configured = plan.ConfigurationValues![RecipeNodeIdentity.Child(call.Id, "out")].Outputs["value"];
            Assert.True(Assert.Single(PcSelectionValue.Read(configured)).AllHosts);
            return;
        }
        ExecutionReport report = await fixture.Engine.ExecuteAsync(SubworkflowTests.Compile(fixture, new("parent", "Parent", [call], [])), call.Id).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report, ContractJson.Options));
        NodeValue output = SubworkflowTests.Output(report, call.Id, "out");
        Assert.Equal(value.Kind, output.Kind);
        Assert.Equal(value.Binary.ToArray(), output.Binary.ToArray());
    }

    /// <summary>失敗経路の抽出を境界で一度だけ行い、外側のfan-outへ配信します。</summary>
    /// <returns>失敗を隠さず、そのデータを明示した後続へ渡す確認です。</returns>
    [Fact]
    public async Task FailureRouteAndPathReachEveryOutsideConsumerAsync()
    {
        WorkflowFixture fixture = Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition action = new("action", "test.recipeFailure", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition recipe = new("recipe", "Recipe", [action, Field("out", true, "answer", "number")], [new(action.Id, "value", "out", "value", "failure", "/part")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        NodeDefinition call = new("group", SubworkflowInterface.CallType(recipe.Id), JsonSerializer.SerializeToElement(new { }));
        NodeDefinition sink = new("sink", "control.pass", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition parent = new("parent", "Parent", [call, sink, sink with { Id = "other" }], [new(call.Id, "answer", sink.Id, "value"), new(call.Id, "answer", "other", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(SubworkflowTests.Compile(fixture, parent)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        foreach (string id in new[] { "sink", "other" })
        {
            NodeResult result = Assert.Single(Assert.Single(report.Nodes, node => string.Equals(node.NodeId, id, StringComparison.Ordinal)).Targets).Result;
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(7, result.Outputs["value"].Json.GetInt32());
        }
    }

    /// <summary>共通の値境界と経路試験ノードを持つ実ランタイムです。</summary>
    /// <returns>独立した実行と保存先です。</returns>
    private static WorkflowFixture Create()
    {
        WorkflowFixture fixture = new(new SubworkflowBoundaryNode(false), new SubworkflowBoundaryNode(true), new PassNode(), new FailureNode());
        fixture.Registry.Dynamic = new SubworkflowCatalog(fixture.Registry, fixture.Sources.GetEffectiveWorkflows);
        return fixture;
    }

    /// <summary>指定型の公開境界です。</summary>
    /// <returns>デフォルトを持たない入力または出力です。</returns>
    private static NodeDefinition Field(string id, bool output, string name, string type) => new(id, output ? SubworkflowInterface.OutputType
                                                                                                            : SubworkflowInterface.InputType,
        JsonSerializer.SerializeToElement(new { name, valueType = new { type } }));

    /// <summary>失敗時だけ出力を持つ実行ノードです。</summary>
    private sealed class FailureNode : IActionNode
    {
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.recipeFailure", "Failure", "Test", "{}", inputs: []);

        /// <summary>設定を必要としません。</summary>
        /// <returns>空の診断です。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>明示失敗経路にデータを付けます。</summary>
        /// <returns>後続へ渡せる失敗結果です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(NodeResult.Failure("fixture", "Fixture failure") with { Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { part = 7 }) } });
    }
}
