using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>Sourceを含むレシピを親WFの通常グラフとして実行します。</summary>
public sealed class InlineRecipeTests
{
    /// <summary>定義変更を次の待受へ反映し、参照削除で依存する配置だけを止めます。</summary>
    /// <returns>無関係なSourceを止めない更新と削除の確認です。</returns>
    [Fact]
    public async Task RecipeChangesRefreshDependentSourcesOnlyAsync()
    {
        RecipeSource source = new();
        WorkflowFixture fixture = new(source, new SubworkflowBoundaryNode(false), new SubworkflowBoundaryNode(true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Registry.Dynamic = new SubworkflowCatalog(fixture.Registry, fixture.Sources.GetEffectiveWorkflows);
        NodeDefinition input = new("in", SubworkflowInterface.InputType, JsonSerializer.SerializeToElement(new { name = "port", valueType = new { type = "integer" }, hasDefault = true, defaultValue = 5 }));
        NodeDefinition listener = new("listen", source.Descriptor.Type, JsonSerializer.SerializeToElement(new { }), StartOnLoad: true);
        WorkflowDefinition recipe = new("recipe", "Recipe", [input, listener], [new("in", "value", "listen", "$settings.port")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        NodeDefinition call = new("group", SubworkflowInterface.CallType(recipe.Id), JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition parent = new("parent", "Parent", [call], [], Enabled: true);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, parent)).ConfigureAwait(true);
        NodeDefinition other = listener with { Id = "other", Settings = JsonSerializer.SerializeToElement(new { port = 40 }) };
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, new("unrelated", "Unrelated", [other], [], Enabled: true))).ConfigureAwait(true);
        string id = RecipeNodeIdentity.Child(call.Id, listener.Id);
        Assert.Equal(5, source.Contexts[id].Initial.Settings.GetProperty("port").GetInt32());
        recipe = recipe with { Nodes = [input with { Settings = JsonSerializer.SerializeToElement(new { name = "port", valueType = new { type = "integer" }, hasDefault = true, defaultValue = 9 }) }, listener] };
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        string portProperty = "port";
        while (!source.Contexts.TryGetValue(id, out NodeSourceContext? context) || context.Initial.Settings.GetProperty(portProperty).GetInt32() != 9)
        {
            await Task.Delay(10, deadline.Token).ConfigureAwait(true);
            await fixture.Sources.ReconcileAsync(deadline.Token).ConfigureAwait(true);
        }
        Assert.Equal(40, source.Contexts[other.Id].Initial.Settings.GetProperty("port").GetInt32());
        await fixture.Sources.RemoveAsync(recipe.Id).ConfigureAwait(true);
        while (source.Contexts.ContainsKey(id))
        {
            await Task.Delay(10, deadline.Token).ConfigureAwait(true);
        }
        Assert.True(source.Contexts.ContainsKey(other.Id));
        CompiledWorkflow blocked = fixture.Sources.GetEffective(parent.Id)!;
        Assert.NotEmpty(blocked.DependencyIssues);
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Engine.ExecuteAsync(blocked).ConfigureAwait(true)).Outcome);
    }

    /// <summary>定義自体を起動せず、二つの配置の設定・入力・出力・停止を分離します。</summary>
    /// <returns>実SourceSupervisorと実グラフ配送での確認です。</returns>
    [Fact]
    public async Task SourcesAreInstantiatedPerPlacementAndPublicSettingsBindAsync()
    {
        RecipeSource source = new();
        Recorder recorder = new();
        WorkflowFixture fixture = new(source, recorder, new SubworkflowBoundaryNode(false), new SubworkflowBoundaryNode(true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Registry.Dynamic = new SubworkflowCatalog(fixture.Registry, fixture.Sources.GetEffectiveWorkflows);
        NodeDefinition input = Field("in", false, "port"), output = Field("out", true, "number");
        NodeDefinition listener = new("listen", source.Descriptor.Type, JsonSerializer.SerializeToElement(new { }), StartOnLoad: true);
        WorkflowDefinition recipe = new("recipe", "Source recipe", [input, listener, output], [new("in", "value", "listen", "$settings.port"), new("listen", "value", "out", "value")], Enabled: true);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        Assert.Empty(source.Contexts);
        NodeDefinition first = Placement("first", recipe.Id, 10), second = Placement("second", recipe.Id, 20);
        NodeDefinition left = new("left", recorder.Descriptor.Type, JsonSerializer.SerializeToElement(new { })), right = left with { Id = "right" };
        WorkflowDefinition parent = new("parent", "Parent", [first, second, left, right], [new("first", "number", "left", "value"), new("second", "number", "right", "value")], Enabled: true);
        CompiledWorkflow plan = SubworkflowTests.Compile(fixture, parent);
        Assert.Equal(ContractJson.Hash(parent), ContractJson.Hash(plan.Definition));
        Assert.Equal(2, plan.RecipeInstances.Count);
        Assert.DoesNotContain(plan.OrderedNodes, node => SubworkflowInterface.Target(node.Type) is not null);
        await fixture.Sources.ApplyAsync(plan).ConfigureAwait(true);
        string firstId = RecipeNodeIdentity.Child("first", "listen"), secondId = RecipeNodeIdentity.Child("second", "listen");
        Assert.Equal(10, source.Contexts[firstId].Initial.Settings.GetProperty("port").GetInt32());
        Assert.Equal(20, source.Contexts[secondId].Initial.Settings.GetProperty("port").GetInt32());
        await source.EmitAsync(firstId, 11).ConfigureAwait(true);
        await source.EmitAsync(secondId, 22).ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (recorder.Values.Count < 2)
        {
            await Task.Delay(10, deadline.Token).ConfigureAwait(true);
        }
        Assert.Equal(11, recorder.Values["left"]);
        Assert.Equal(22, recorder.Values["right"]);
        Assert.All(source.Contexts.Values, context => Assert.Equal(parent.Id, context.Initial.WorkflowId));
        await fixture.Sources.RemoveAsync(parent.Id).ConfigureAwait(true);
        while (!source.Contexts.IsEmpty)
        {
            await Task.Delay(10, deadline.Token).ConfigureAwait(true);
        }
    }

    /// <summary>公開名を持つ出力が親の後続ノードへ直接届きます。</summary>
    /// <returns>値が親側の利用ノードへ届くことの確認です。</returns>
    [Fact]
    public async Task PublicOutputReachesTheContainingWorkflowAsync()
    {
        WorkflowFixture fixture = SubworkflowTests.Create();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition recipe = new("text", "Text", [SubworkflowTests.Field("in", false, "text"), SubworkflowTests.Field("out", true, "answer")], [new("in", "value", "out", "value")]);
        await fixture.Sources.ApplyAsync(SubworkflowTests.Compile(fixture, recipe)).ConfigureAwait(true);
        NodeDefinition placement = new("group", SubworkflowInterface.CallType(recipe.Id), JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["text"] = NodeValue.FromObject("hello") });
        NodeDefinition after = new("after", "text.substring", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["startIndex"] = NodeValue.FromObject(1), ["length"] = NodeValue.FromObject(3) });
        CompiledWorkflow plan = SubworkflowTests.Compile(fixture, new("parent", "Parent", [placement, after], [new("group", "answer", "after", "value")]));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(plan).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal("ell", Assert.Single(Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "after", StringComparison.Ordinal)).Targets).Result.Outputs["value"].GetString());
        Assert.DoesNotContain(fixture.Engine.Recent(), item => string.Equals(item.WorkflowId, recipe.Id, StringComparison.Ordinal));
    }

    /// <summary>入力と出力の型を整数として公開します。</summary>
    /// <returns>レシピ境界の定義です。</returns>
    private static NodeDefinition Field(string id, bool output, string name) => new(id, output ? SubworkflowInterface.OutputType
                                                                                               : SubworkflowInterface.InputType,
        JsonSerializer.SerializeToElement(new { name, valueType = new { type = "integer" }, hasDefault = false }));

    /// <summary>同じレシピへ別々の値を渡します。</summary>
    /// <returns>独立した配置です。</returns>
    private static NodeDefinition Placement(string id, string recipe, int port) => new(id, SubworkflowInterface.CallType(recipe), JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["port"] = NodeValue.FromObject(port) });

    /// <summary>親の実行機構へ値を渡す制御可能な待受です。</summary>
    private sealed class RecipeSource : ISourceNode
    {
        internal ConcurrentDictionary<string, NodeSourceContext> Contexts
        {
            get;
        } = new(StringComparer.Ordinal);

        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.recipeSource", "Source", "Test", """{"type":"object","required":["port"],"properties":{"port":{"type":"integer","minimum":1}}}""", inputs: [], kind: ENodeKind.Source);

        /// <summary>この試験ノードの設定を検証します。</summary>
        /// <returns>不正な設定です。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSchemaValidator.Validate(definition, Descriptor);

        /// <summary>停止まで待ち、配置ごとの待受文脈を公開します。</summary>
        /// <returns>待受自身の終了です。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            Contexts[context.Initial.NodeId] = context;
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
            await context.ReportStatusAsync(new(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.IsStopRequested)
            {
                return NodeResult.Success();
            }
            finally
            {
                Contexts.TryRemove(context.Initial.NodeId, out _);
            }
            return NodeResult.Success();
        }

        /// <summary>指定配置の待受から一件を送出します。</summary>
        /// <returns>入力受付の完了です。</returns>
        internal ValueTask EmitAsync(string id, int value, CancellationToken cancellationToken = default)
        {
            NodeSourceContext context = Contexts[id];
            return context.EmitAsync(context.BeginItem(), NodeResult.Success(NodeValue.FromObject(value)), cancellationToken);
        }
    }

    /// <summary>親グラフの利用先で値を観測します。</summary>
    private sealed class Recorder : IActionNode
    {
        internal ConcurrentDictionary<string, int> Values
        {
            get;
        } = new(StringComparer.Ordinal);

        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.recorder", "Recorder", "Test", "{\"type\":\"object\",\"properties\":{}}");

        /// <summary>この試験ノードの設定を検証します。</summary>
        /// <returns>不正な設定です。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>実際に到達した親側の入力を記録します。</summary>
        /// <returns>受信した値です。</returns>
        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
        {
            Values[context.NodeId] = context.Inputs["value"].Json.GetInt32();
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"]));
        }
    }
}
