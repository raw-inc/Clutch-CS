using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>配列選択と乱数の型・設定をエディタ上で正しく表示します。</summary>
public sealed class ArrayPickEditorTests
{
    /// <summary>辞書を接続した両選択ノードに、キー・値の型を表示します。</summary>
    [Theory]
    [InlineData("data.pick")]
    [InlineData("data.randomPick")]
    public void DictionarySelectionDisplaysPairTypes(string type)
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        NodeDefinition pick = new("pick", type, settings, new Dictionary<string, NodeValue>
        {
            ["value"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("{\"A\":1,\"B\":2}")), ["index"] = NodeValue.FromObject(1),
        });
        GraphTypeInference inference = new(new("flow", "Selection", [pick], []), [Describe(type)]);
        GraphPortPresentation output = inference.Port("pick", "value", true);
        Assert.Equal("object", output.InferredType);
        Assert.Equal("string", output.DataType!.KeyType!.Type);
        Assert.Equal("integer", output.DataType.ValueType!.Type);
    }

    /// <summary>Indexと整数／小数モードの変更を表示型へ反映します。</summary>
    [Fact]
    public void SelectionAndModeDetermineTheDisplayedOutputType()
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        NodeDefinition pick = new("pick", "data.pick", settings, new Dictionary<string, NodeValue>
        {
            ["value"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("[42,\"text\"]")), ["index"] = NodeValue.FromObject(1),
        });
        NodeDefinition random = new("random", "math.random", settings);
        NodeDefinition randomPick = new("randomPick", "data.randomPick", settings, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("[1,2]")) });
        NodeDescriptor[] catalog = [Describe("data.pick"), Describe("data.randomPick"), Describe("math.random")];
        WorkflowDefinition document = new("flow", "Selection", [pick, random, randomPick], []);
        GraphTypeInference initial = new(document, catalog);
        Assert.Equal("string", initial.Port("pick", "value", true).InferredType);
        Assert.Equal("integer", initial.Port("random", "value", true).InferredType);
        Assert.Equal("integer", initial.Port("randomPick", "value", true).InferredType);
        pick = pick with { Inputs = new Dictionary<string, NodeValue>(pick.Inputs!) { ["index"] = NodeValue.FromObject(0) } };
        random = random with { Settings = JsonSerializer.SerializeToElement(new { mode = "decimal" }) };
        GraphTypeInference changed = new(document with { Nodes = [pick, random, randomPick] }, catalog);
        Assert.Equal("integer", changed.Port("pick", "value", true).InferredType);
        Assert.Equal("number", changed.Port("random", "value", true).InferredType);
        Assert.Equal("array", changed.Port("pick", "value", false).InferredType);
        Assert.Equal("integer", changed.Port("pick", "index", false).InferredType);
    }

    /// <summary>Indexの起点と乱数の範囲端を、両言語で曖昧にしません。</summary>
    /// <returns>実際に読込む翻訳辞書の確認です。</returns>
    [Fact]
    public async Task LabelsExplainIndexAndEndpointsAsync()
    {
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("Index (starting at 0)", text.PortName("data.pick", "index", false));
        Assert.Equal("Decimal (upper bound excluded)", text.SettingChoiceName("math.random", "mode", "decimal"));
        Assert.Equal("Minimum", text.SettingName("minimum"));
        await text.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("Index（0始まり）", text.PortName("data.pick", "index", false));
        Assert.Equal("整数（両端を含む）", text.SettingChoiceName("math.random", "mode", "integer"));
        Assert.Equal("最大値", text.SettingName("maximum"));
    }

    /// <summary>画面が受け取るカタログの入出力契約です。</summary>
    /// <returns>配列・Indexの型を保持した宣言です。</returns>
    private static NodeDescriptor Describe(string type) => new(type, type, "Data", "", ENodeKind.Action, EExecutionLocation.Independent,
        [new("value", EPortValueKind.Json), new("index", EPortValueKind.Json) { DataType = new("integer") }],
        [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
