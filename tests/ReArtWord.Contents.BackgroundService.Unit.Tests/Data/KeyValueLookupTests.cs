using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>両形式・欠落・null・型推論を同じキー検索の契約で確認します。</summary>
public sealed class KeyValueLookupTests
{
    /// <summary>自動判別と明示形式で値を変換せず取得します。</summary>
    /// <returns>既存Object配列と小文字・大文字ペアの確認です。</returns>
    [Theory]
    [InlineData("[{\"A\":1},{\"B\":2}]", "B", "auto", "2")]
    [InlineData("{\"A\":1,\"B\":2}", "B", "auto", "2")]
    [InlineData("[{\"A\":1,\"B\":2}]", "A", "objects", "1")]
    [InlineData("[{\"key\":\"A\",\"value\":true}]", "A", "auto", "true")]
    [InlineData("[{\"Key\":\"A\",\"Value\":false}]", "A", "pairs", "false")]
    [InlineData("[{\"A\":1},{\"key\":\"B\",\"value\":\"001\"}]", "B", "auto", "\"001\"")]
    [InlineData("[{\"key\":\"A\",\"value\":1}]", "key", "objects", "\"A\"")]
    [InlineData("[{\"a/b~c\":{\"nested\":[1,2]}}]", "a/b~c", "auto", "{\"nested\":[1,2]}")]
    [InlineData("[{\"\":0}]", "", "auto", "0")]
    [InlineData("[{\"日本語\":\"保持\"}]", "日本語", "auto", "\"保持\"")]
    public async Task SelectsValuesWithoutCoercionAsync(string entries, string key, string format, string expected)
    {
        NodeResult result = await RunAsync(entries, key, format, NodeValue.FromObject("fallback")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(JsonElement.DeepEquals(Json(expected).Json, result.Outputs["value"].Json));
    }

    /// <summary>存在するnullとキー欠落を区別し、既定値の型も保持します。</summary>
    /// <returns>欠落・大小文字・null・バイト列の確認です。</returns>
    [Fact]
    public async Task DefaultIsUsedOnlyForMissingKeysAsync()
    {
        NodeResult found = await RunAsync("[{\"A\":null}]", "A", fallback: NodeValue.FromObject(42)).ConfigureAwait(true);
        Assert.Equal(JsonValueKind.Null, found.Outputs["value"].Json.ValueKind);
        NodeResult absent = await RunAsync("[{\"A\":1}]", "a", fallback: NodeValue.FromObject(false)).ConfigureAwait(true);
        Assert.False(absent.Outputs["value"].Json.GetBoolean());
        NodeResult omitted = await RunAsync("[]", "missing").ConfigureAwait(true);
        Assert.Equal(JsonValueKind.Null, omitted.Outputs["value"].Json.ValueKind);
        NodeValue bytes = NodeValue.FromBytes(new byte[] { 1, 2, 3 });
        NodeResult binary = await RunAsync("[]", "missing", fallback: bytes).ConfigureAwait(true);
        Assert.Equal(EValueKind.Binary, binary.Outputs["value"].Kind);
        Assert.Equal(bytes.Binary.ToArray(), binary.Outputs["value"].Binary.ToArray());
        Assert.NotSame(bytes, binary.Outputs["value"]);
    }

    /// <summary>曖昧な重複と不正入力を既定値へ隠しません。</summary>
    /// <returns>失敗経路と未出力の確認です。</returns>
    [Theory]
    [InlineData("[{\"A\":1},{\"key\":\"A\",\"value\":2}]", "auto", "value.lookup.key.duplicate")]
    [InlineData("[{\"A\":1,\"A\":2}]", "objects", "value.lookup.key.duplicate")]
    [InlineData("1", "auto", "value.lookup.input.array")]
    [InlineData("[null]", "auto", "value.lookup.entry.object")]
    [InlineData("[{\"key\":1,\"value\":2}]", "auto", "value.lookup.entry.pair")]
    [InlineData("[{\"key\":\"A\"}]", "pairs", "value.lookup.entry.pair")]
    [InlineData("[{\"key\":\"A\",\"value\":1,\"extra\":2}]", "pairs", "value.lookup.entry.pair")]
    public async Task InvalidOrAmbiguousInputsFailAsync(string entries, string format, string reason)
    {
        NodeResult result = await RunAsync(entries, "A", format, NodeValue.FromObject("fallback")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>既存Key-ValueノードとArrayノードの実出力をそのまま検索します。</summary>
    /// <returns>Path意味付けと通常の評価にも同じ結果を使う確認です。</returns>
    [Fact]
    public async Task ExistingKeyValueAndArrayNodesWorkWithoutReshapingAsync()
    {
        NodeValue path = NodeValue.FromObject(Path.Combine(Path.GetTempPath(), "lookup.txt")) with { DataType = new("path") };
        NodeResult pair = await new KeyValueNode().ExecuteAsync(NodeTestContext.Create(value: path,
            extra: new Dictionary<string, NodeValue> { ["key"] = NodeValue.FromObject("asset") })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, pair.Outcome);
        NodeResult array = await new ArrayValueNode().ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue> { ["item"] = pair.Outputs["value"] })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, array.Outcome);
        Dictionary<string, NodeValue> inputs = new() { ["value"] = array.Outputs["value"], ["key"] = NodeValue.FromObject("asset") };
        KeyValueLookupNode lookup = new();
        NodeResult result = lookup.Evaluate(new("get", lookup.Descriptor.Type, JsonSerializer.SerializeToElement(new { })), inputs);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(pair.Outputs["value"].Json.GetProperty("asset").GetString(), result.Outputs["value"].GetString());
        Assert.Equal("path", result.Outputs["value"].DataType!.Type);
        Assert.True(new NodeRegistry([lookup]).Descriptors.Single().CanEvaluateStatically);
    }

    /// <summary>キーと配列が既知なら、選ばれない既定値で出力型を曖昧にしません。</summary>
    [Theory]
    [InlineData("[{\"A\":1},{\"B\":\"text\"}]", "A", "integer")]
    [InlineData("[{\"A\":1},{\"B\":\"text\"}]", "B", "string")]
    [InlineData("[{\"key\":\"A\",\"value\":true}]", "A", "boolean")]
    [InlineData("[]", "missing", "string")]
    public void FixedKeysInferTheSelectedValue(string entries, string key, string expected)
    {
        KeyValueLookupNode lookup = new();
        NodeDefinition node = new("get", lookup.Descriptor.Type, JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>
        { ["value"] = Json(entries), ["key"] = NodeValue.FromObject(key), ["defaultValue"] = NodeValue.FromObject("fallback") });
        WorkflowTypeInference inference = new(new("flow", "Lookup", [node], []), [lookup.Descriptor]);
        Assert.Equal(expected, inference.PortType("get", "value", true)!.Type);
    }

    /// <summary>動的キーでは、検索値と既定値の両方を候補に含めます。</summary>
    [Theory]
    [InlineData("[{\"A\":1},{\"B\":2}]", "0", "integer", false)]
    [InlineData("[{\"A\":1},{\"B\":2}]", "null", "integer", true)]
    [InlineData("[{\"A\":1},{\"B\":2.5}]", "0", "number", false)]
    [InlineData("[{\"A\":1},{\"B\":\"text\"}]", "0", "any", false)]
    [InlineData("[]", "false", "boolean", false)]
    public void DynamicKeysIncludeTheDefaultType(string entries, string fallback, string expected, bool optional)
    {
        KeyValueLookupNode lookup = new();
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        NodeDefinition node = new("get", lookup.Descriptor.Type, settings, new Dictionary<string, NodeValue> { ["value"] = Json(entries), ["defaultValue"] = Json(fallback) });
        NodeDescriptor source = new("external.key", "Key", "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value", EPortValueKind.Json) { DataType = new("string") }], ["success"], settings);
        WorkflowTypeInference inference = new(new("flow", "Lookup", [node, new("key", source.Type, settings)], [new("key", "value", "get", "key")]), [lookup.Descriptor, source]);
        ValueTypeDefinition result = inference.PortType("get", "value", true)!;
        Assert.Equal(expected, result.Type);
        Assert.Equal(optional, result.Optional);
    }

    /// <summary>既存ノードを接続して組み立てた配列の値も静的に選択できます。</summary>
    [Fact]
    public void InfersThroughConnectedKeyValueAndArrayNodes()
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        NodeDefinition pair = new("pair", "value.keyValue", settings, new Dictionary<string, NodeValue> { ["key"] = NodeValue.FromObject("A"), ["value"] = NodeValue.FromObject(42) });
        NodeDefinition array = new("array", "value.array", settings);
        NodeDefinition lookup = new("get", KeyValueLookup.NodeType, settings, new Dictionary<string, NodeValue> { ["key"] = NodeValue.FromObject("A"), ["defaultValue"] = NodeValue.FromObject("fallback") });
        WorkflowDefinition document = new("flow", "Lookup", [pair, array, lookup], [new("pair", "value", "array", "item"), new("array", "value", "get", "value")]);
        WorkflowTypeInference inference = new(document, new NodeRegistry([new KeyValueNode(), new ArrayValueNode(), new KeyValueLookupNode()]).Descriptors);
        Assert.Equal("integer", inference.PortType("get", "value", true)!.Type);
    }

    /// <summary>外部の宣言型は形式を固定した場合だけ候補型として使用します。</summary>
    [Theory]
    [InlineData("pairs", "integer")]
    [InlineData("auto", "any")]
    public void DeclaredPairValuesAreInferredWithExplicitFormat(string format, string expected)
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        KeyValueLookupNode lookup = new();
        NodeDefinition node = new("get", lookup.Descriptor.Type, JsonSerializer.SerializeToElement(new { format }), new Dictionary<string, NodeValue>
        { ["key"] = NodeValue.FromObject("A"), ["defaultValue"] = NodeValue.FromObject(0) });
        NodeDescriptor source = new("external.pairs", "Pairs", "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [],
            [new("value", EPortValueKind.Json) { DataType = new("array", new("object", Properties: new Dictionary<string, ValueTypeDefinition> { ["Key"] = new("string"), ["Value"] = new("integer") })) }], ["success"], settings);
        WorkflowTypeInference inference = new(new("flow", "Lookup", [node, new("source", source.Type, settings)], [new("source", "value", "get", "value")]), [lookup.Descriptor, source]);
        Assert.Equal(expected, inference.PortType("get", "value", true)!.Type);
    }

    /// <summary>実入力と同じ表現で検索を呼び出します。</summary>
    /// <returns>通常実行の結果です。</returns>
    private static ValueTask<NodeResult> RunAsync(string entries, string key, string format = "auto", NodeValue? fallback = null)
    {
        Dictionary<string, NodeValue> inputs = new() { ["key"] = NodeValue.FromObject(key) };
        if (fallback is not null)
        {
            inputs["defaultValue"] = fallback;
        }
        return new KeyValueLookupNode().ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { format }), Json(entries), inputs));
    }

    /// <summary>テスト側で与えたJSON構造を値として保持します。</summary>
    /// <returns>変換されていない入力です。</returns>
    private static NodeValue Json(string value) => NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(value));
}
