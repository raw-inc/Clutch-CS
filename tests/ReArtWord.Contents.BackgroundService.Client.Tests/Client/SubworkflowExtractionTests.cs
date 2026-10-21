using System.Net;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>公開境界の自動生成と、草稿保存前後の文書保持を検証します。</summary>
public sealed class SubworkflowExtractionTests
{
    /// <summary>一時的な境界ノードから親へ永続的なレシピ指定を付けません。</summary>
    /// <returns>境界を削除した後も、利用者の明示指定だけを保持する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractingDoesNotLatchInferredParentKindAsync(bool explicitRecipe)
    {
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        WorkflowDefinition original = Document();
        NodeDefinition boundary = new("public", SubworkflowInterface.InputType, JsonSerializer.SerializeToElement(new { name = "public", valueType = new { type = "string" } }));
        original = original with { Nodes = [.. original.Nodes, boundary], IsRecipe = explicitRecipe };
        GraphExtractionPlan plan = new SubworkflowExtraction(original, new HashSet<string>(["one", "two"], StringComparer.Ordinal), Catalog(), text).Create();
        Assert.Equal(explicitRecipe, plan.Parent.IsRecipe);
        WorkflowDefinition withoutBoundary = plan.Parent with { Nodes = plan.Parent.Nodes.Where(node => !SubworkflowInterface.IsBoundary(node.Type)).ToArray() };
        Assert.Equal(explicitRecipe, SubworkflowInterface.IsSubworkflow(withoutBoundary));
    }

    /// <summary>境界線、未接続の必須入力・設定、fan-outと抽出条件を保持します。</summary>
    /// <returns>公開名・型・位置と接続の確認です。</returns>
    [Fact]
    public async Task ExtractsPublicPortsAndReconnectsBothDirectionsAsync()
    {
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        WorkflowDefinition original = Document();
        GraphExtractionPlan plan = new SubworkflowExtraction(original, new HashSet<string>(["one", "two"], StringComparer.Ordinal), Catalog(), text).Create();
        SubworkflowInterface contract = SubworkflowInterface.Read(plan.Recipe);
        Assert.Equal(3, contract.Inputs.Count);
        Assert.Equal(5, contract.Outputs.Count);
        Assert.Equal(5, contract.Outputs.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(contract.Inputs.Concat(contract.Outputs), field => Assert.Contains(" - ", field.Name, StringComparison.Ordinal));
        double[] outputRows = contract.Outputs.Select(field => plan.Recipe.Nodes.Single(node => string.Equals(node.Id, field.NodeId, StringComparison.Ordinal)).Position!.Y).Order().ToArray();
        Assert.All(outputRows.Zip(outputRows.Skip(1)), pair => Assert.True(500 <= pair.Second - pair.First));
        NodeDefinition one = Assert.Single(plan.Recipe.Nodes, node => string.Equals(node.Id, "one", StringComparison.Ordinal));
        NodeDefinition two = Assert.Single(plan.Recipe.Nodes, node => string.Equals(node.Id, "two", StringComparison.Ordinal));
        Assert.Equal(new NodePosition(520, 80), one.Position);
        Assert.Equal(new NodePosition(420, 280), two.Position);
        Assert.Equal(3, two.Inputs!["required"].Json.GetInt32());
        Assert.Contains(plan.Recipe.Wires, wire => string.Equals(wire.FromNode, "one", StringComparison.Ordinal) && string.Equals(wire.ToNode, "two", StringComparison.Ordinal) && string.Equals(wire.FromPort, "value", StringComparison.Ordinal) && string.Equals(wire.ToPort, "value", StringComparison.Ordinal));
        WireDefinition inbound = Assert.Single(plan.Parent.Wires, wire => string.Equals(wire.FromNode, "outside", StringComparison.Ordinal));
        Assert.Equal(plan.InstanceId, inbound.ToNode);
        SubworkflowField input = Assert.Single(contract.Inputs, field => string.Equals(field.Name, inbound.ToPort, StringComparison.Ordinal));
        Assert.Contains(plan.Recipe.Wires, wire => string.Equals(wire.FromNode, input.NodeId, StringComparison.Ordinal) && string.Equals(wire.ToNode, "one", StringComparison.Ordinal) && string.Equals(wire.ToPort, "value", StringComparison.Ordinal));
        WireDefinition[] outgoing = plan.Parent.Wires.Where(wire => string.Equals(wire.FromNode, plan.InstanceId, StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, outgoing.Length);
        Assert.Single(outgoing.Select(wire => wire.FromPort).Distinct(StringComparer.Ordinal));
        Assert.All(outgoing, wire =>
        {
            Assert.Equal("success", wire.When);
            Assert.Null(wire.Path);
        });
        SubworkflowField result = Assert.Single(contract.Outputs, field => string.Equals(field.Name, outgoing[0].FromPort, StringComparison.Ordinal));
        WireDefinition resultWire = Assert.Single(plan.Recipe.Wires, wire => string.Equals(wire.ToNode, result.NodeId, StringComparison.Ordinal));
        Assert.Equal("failure", resultWire.When);
        Assert.Equal("/part", resultWire.Path);
        Assert.DoesNotContain(plan.Parent.Nodes, node => node.Id is "one" or "two");
        Assert.Same(original, plan.Original);
    }

    /// <summary>Sourceも配置を含めてコピーでき、子は再利用定義になります。</summary>
    /// <returns>有限処理限定を設けない確認です。</returns>
    [Fact]
    public async Task SourceCanBeExtractedAsARecipeAsync()
    {
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        WorkflowDefinition source = new("parent", "Parent", [new("source", "test.source", JsonSerializer.SerializeToElement(new { }), StartOnLoad: true)], [], Enabled: true);
        GraphExtractionPlan plan = new SubworkflowExtraction(source, new HashSet<string>(["source"], StringComparer.Ordinal), Catalog(), text).Create();
        Assert.True(plan.Recipe.IsRecipe);
        Assert.True(Assert.Single(plan.Recipe.Nodes, node => string.Equals(node.Id, "source", StringComparison.Ordinal)).StartOnLoad);
        Assert.Equal(SubworkflowInterface.CallType(plan.Recipe.Id), Assert.Single(plan.Parent.Nodes).Type);
    }

    /// <summary>子草稿保存を確認してから置換し、一回のUndoと別タブ復元ができます。</summary>
    /// <returns>オフラインの草稿保持も含む確認です。</returns>
    [Fact]
    public async Task RetainsRecipeDraftBeforeReplacingSelectionAsync()
    {
        BrowserTestRuntime runtime = new();
        BrowserStorage storage = new(runtime);
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        model.Import(Document());
        UiText text = new(storage);
        await text.InitializeAsync().ConfigureAwait(true);
        GraphExtractionPlan plan = new SubworkflowExtraction(model.Draft.Document, new HashSet<string>(["one", "two"], StringComparer.Ordinal), Catalog(), text).Create();
        using ClientHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        EditorRecipeDrafts recipes = new(model, storage, new(client));
        await recipes.ExtractAsync(plan).ConfigureAwait(true);
        Assert.Equal(plan.InstanceId, model.SelectedNode);
        Assert.Equal(plan.Recipe.Id, Assert.Single(recipes.Unsaved).Id);
        Assert.Equal(SubworkflowInterface.CallType(plan.Recipe.Id), Assert.Single(recipes.Catalog).Type);
        EditorDocument reloaded = new(storage);
        await reloaded.InitializeAsync().ConfigureAwait(true);
        Assert.Equal(ContractJson.Hash(plan.Parent), ContractJson.Hash(reloaded.Draft.Document));
        model.Undo();
        Assert.Equal(ContractJson.Hash(plan.Original), ContractJson.Hash(model.Draft.Document));
        Assert.True(await model.TryOpenDraftAsync(plan.Recipe.Id).ConfigureAwait(true));
        Assert.Equal(ContractJson.Hash(plan.Recipe), ContractJson.Hash(model.Draft.Document));
    }

    /// <summary>子の書込みだけが失敗しても、親の本文とreload先を変えません。</summary>
    /// <returns>保存失敗時に元WFを復元できる確認です。</returns>
    [Fact]
    public async Task ChildStorageFailureLeavesParentAndReloadTargetUntouchedAsync()
    {
        BrowserTestRuntime runtime = new();
        BrowserStorage storage = new(runtime);
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        model.Import(Document());
        await model.PersistAsync().ConfigureAwait(true);
        UiText text = new(storage);
        await text.InitializeAsync().ConfigureAwait(true);
        GraphExtractionPlan plan = new SubworkflowExtraction(model.Draft.Document, new HashSet<string>(["one", "two"], StringComparer.Ordinal), Catalog(), text).Create();
        string prefix = "contents.blazor.draft.";
        runtime.RejectWrite = (key, value) => key.StartsWith(prefix, StringComparison.Ordinal) && JsonSerializer.Deserialize<EditorDraft>(value, ContractJson.Options)?.Document.IsRecipe == true;
        using ClientHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        EditorRecipeDrafts recipes = new(model, storage, new(client));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recipes.ExtractAsync(plan)).ConfigureAwait(true);
        Assert.Same(plan.Original, model.Draft.Document);
        Assert.Equal(plan.Original.Id, await EditorDraftStorage.ReadLastAsync(storage, model.ClusterId, model.PeerId).ConfigureAwait(true));
        runtime.RejectWrite = null;
        EditorDocument reopened = new(storage);
        await reopened.InitializeAsync().ConfigureAwait(true);
        Assert.Equal(ContractJson.Hash(plan.Original), ContractJson.Hash(reopened.Draft.Document));
    }

    /// <summary>外側の接続元が保護中なら、再接続によるロック回避を拒否します。</summary>
    /// <returns>生成先の書込み前に停止する確認です。</returns>
    [Fact]
    public async Task BoundaryReconnectionHonorsLocksAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        model.Import(Document());
        UiText text = new(storage);
        await text.InitializeAsync().ConfigureAwait(true);
        GraphExtractionPlan plan = new SubworkflowExtraction(model.Draft.Document, new HashSet<string>(["one", "two"], StringComparer.Ordinal), Catalog(), text).Create();
        model.Locks.Update([new(Guid.NewGuid(), plan.Original.Id, ["outside"])]);
        using HttpClient client = new() { BaseAddress = new Uri("http://localhost/") };
        EditorRecipeDrafts recipes = new(model, storage, new(client));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recipes.ExtractAsync(plan)).ConfigureAwait(true);
        Assert.Same(plan.Original, model.Draft.Document);
    }

    /// <summary>必須の通常入力と設定、および未接続出力を持つ定義です。</summary>
    /// <returns>グラフの編集用宣言です。</returns>
    private static NodeDescriptor[] Catalog()
    {
        ValueTypeDefinition type = new("object", Properties: new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal) { ["part"] = new("number") });
        NodeDescriptor worker = new("test.worker", "Worker", "Test", "", ENodeKind.Action, EExecutionLocation.Independent,
            [new("value", Required: true), new("required", Required: true)], [new("value") { DataType = type }, new("extra") { DataType = new("number") }], ["success"],
            JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"required\":[\"threshold\"],\"properties\":{\"threshold\":{\"type\":\"number\"}}}"));
        return [worker, worker with { Type = "test.source", Kind = ENodeKind.Source, Inputs = [], SettingsSchema = JsonSerializer.Deserialize<JsonElement>("{}") }];
    }

    /// <summary>選択外への二本の線は同じ出力のfan-outです。</summary>
    /// <returns>配線条件を保持して分離するサンプルです。</returns>
    private static WorkflowDefinition Document()
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        return new("parent", "Parent", [new("outside", "test.source", empty), new("one", "test.worker", empty, Position: new(200, 100)),
            new("two", "test.worker", JsonSerializer.SerializeToElement(new { threshold = 2 }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["required"] = NodeValue.FromObject(3) }, Position: new(100, 300)),
            new("sink", "test.worker", empty), new("sink2", "test.worker", empty)],
            [new("outside", "value", "one", "value"), new("one", "value", "two", "value"), new("two", "value", "sink", "value", "failure", "/part"), new("two", "value", "sink2", "value", "failure", "/part")]);
    }
}
