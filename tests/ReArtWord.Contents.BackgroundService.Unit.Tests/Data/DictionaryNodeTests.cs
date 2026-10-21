using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>辞書構築の重複方針、型推論、既存ノードとの組合せを確認します。</summary>
public sealed class DictionaryNodeTests
{
    /// <summary>元のKey-ValueをArrayでまとめ、その出力を変更せず辞書へ渡せます。</summary>
    /// <returns>独立した辞書、空辞書、追加・上書きと静的型の確認です。</returns>
    [Fact]
    public async Task BuildsFromKeyValueArraysAndRejectsDuplicatesAsync()
    {
        NodeResult first = await new KeyValueNode().ExecuteAsync(NodeTestContext.Create("{\"key\":\"01\"}", NodeValue.FromObject(7)));
        NodeResult array = await new ArrayValueNode().ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue> { ["item"] = first.Outputs["value"] }));
        DictionaryValueNode node = new();
        NodeDefinition definition = new("dict", node.Descriptor.Type, JsonSerializer.SerializeToElement(new { }));
        Dictionary<string, NodeValue> inputs = new() { ["items"] = array.Outputs["value"], ["item"] = Json("{\"1\":8}") };
        NodeResult result = node.Evaluate(definition, inputs);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(7, result.Outputs["value"].Json.GetProperty("01").GetInt32());
        Assert.Equal(8, result.Outputs["value"].Json.GetProperty("1").GetInt32());
        Assert.Equal("string", result.Outputs["value"].DataType!.KeyType!.Type);
        Assert.Equal("integer", result.Outputs["value"].DataType!.ValueType!.Type);
        Assert.Single(array.Outputs["value"].Json.EnumerateArray());
        inputs["item"] = Json("{\"01\":9}");
        Assert.Equal("value.key.duplicate", node.Evaluate(definition, inputs).ReasonCode);
        NodeResult replaced = node.Evaluate(definition with { Settings = JsonSerializer.SerializeToElement(new { duplicate = "replace" }) }, inputs);
        Assert.Equal(9, replaced.Outputs["value"].Json.GetProperty("01").GetInt32());
        Assert.Equal(7, first.Outputs["value"].Json.GetProperty("01").GetInt32());
        Assert.Empty(node.Evaluate(definition, new Dictionary<string, NodeValue>()).Outputs["value"].Json.EnumerateObject());
    }

    /// <summary>同じObject内の重複も、別項目からの重複と同じ規則で扱います。</summary>
    [Fact]
    public void DuplicateJsonKeysNeverSilentlyPickTheWrongOccurrence()
    {
        NodeValue input = Json("[{\"x\":1,\"x\":2}]");
        Assert.Throws<FormatException>(() => DictionaryValues.Build(input, null));
        Assert.Equal(2, DictionaryValues.Build(input, null, "replace").Json.GetProperty("x").GetInt32());
        Assert.Equal("any", DictionaryValues.Build(Json("[{\"x\":1},{\"y\":\"text\"}]"), null).DataType!.ValueType!.Type);
        Assert.Throws<FormatException>(() => DictionaryValues.Build(Json("[1]"), null));
    }

    /// <summary>多数の項目でも、混在するPathの意味付けを型表示上限で失いません。</summary>
    [Fact]
    public void ManyMixedEntriesKeepPathMetadata()
    {
        NodeValue[] values = Enumerable.Range(0, 150).Select(index =>
        {
            NodeValue source = NodeValue.FromObject(new Dictionary<string, string> { ["key-" + index] = "folder/file.png" });
            string type = "string";
            if (index % 2 == 0)
            {
                type = "path";
            }
            return PathTypeMetadata.Apply(source, new("object", Properties: new Dictionary<string, ValueTypeDefinition> { ["key-" + index] = new(type) }));
        }).ToArray();
        NodeValue dictionary = DictionaryValues.Collect(values, "reject", new("string"));
        Assert.Equal("path", JsonPointer.Select(dictionary, "/key-148").DataType!.Type);
        Assert.Null(JsonPointer.Select(dictionary, "/key-149").DataType);
        NodeValue iteration = DictionaryValues.ForEach(dictionary);
        Assert.Equal("path", JsonPointer.Select(iteration, "/148/key-148").DataType!.Type);
    }

    /// <summary>逐次・並列どちらも辞書から一組ずつ取り出し、Path情報を失いません。</summary>
    /// <returns>元辞書を変更しない反復入力の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothIterationStartsAcceptDictionaryEntriesAsync(bool parallel)
    {
        NodeValue source = PathTypeMetadata.Apply(Json("{\"asset\":\"folder/file.png\",\"count\":4}"), new("object", Properties: new Dictionary<string, ValueTypeDefinition>
        { ["asset"] = new("path"), ["count"] = new("integer") }));
        NodeValue dictionary = DictionaryValues.Build(source, null);
        NodeResult result = await new ForEachNode(parallel).ExecuteAsync(NodeTestContext.Create(value: dictionary));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        NodeValue entries = result.Outputs[IterationNodeTypes.LoopPort];
        Assert.Equal(2, entries.Json.GetArrayLength());
        NodeValue path = JsonPointer.Select(entries, "/0/asset");
        Assert.Equal("path", path.DataType!.Type);
        Assert.Equal(4, entries.Json[1].GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Object, dictionary.Json.ValueKind);
    }

    /// <summary>接続前に辞書の型と、反復内のKey-Value型が一致します。</summary>
    [Fact]
    public void DictionaryTypesFollowConnections()
    {
        DictionaryValueNode builder = new();
        ForEachNode loop = new();
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NodeDefinition dictionary = new("dictionary", builder.Descriptor.Type, empty, new Dictionary<string, NodeValue> { ["items"] = Json("[{\"A\":1},{\"B\":2}]") });
        WorkflowDefinition workflow = new("dictionary", "Dictionary", [dictionary, new("loop", loop.Descriptor.Type, empty)], [new("dictionary", "value", "loop", "value")]);
        NodeDescriptor[] catalog = [builder.Descriptor, loop.Descriptor];
        WorkflowTypeInference inference = new(workflow, catalog);
        Assert.Equal("integer", inference.PortType("dictionary", "value", true)!.ValueType!.Type);
        Assert.Equal("integer", inference.PortType("loop", "item", true)!.ValueType!.Type);
        Assert.True(WorkflowConnectionTypes.CanConnect(workflow, catalog, workflow.Wires[0]));
    }

    /// <summary>JSONのキー重複を入力段階で失わず試験します。</summary>
    /// <returns>JSON値です。</returns>
    private static NodeValue Json(string text) => NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(text));
}
