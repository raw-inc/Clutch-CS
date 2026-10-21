using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>辞書の選択でもキーと値を分離せず、実行・接続検証・表示で同じ型を扱います。</summary>
public sealed class DictionarySelectionTests
{
    /// <summary>辞書から一組を選び、整数キーと入れ子のPathを維持します。</summary>
    /// <returns>抽出ノードにも渡せる一組の辞書です。</returns>
    [Fact]
    public async Task SelectionPreservesKeyAndNestedValueTypesAsync()
    {
        ValueTypeDefinition itemType = new("object", Properties: new Dictionary<string, ValueTypeDefinition> { ["path"] = new("path") });
        NodeValue dictionary = NodeValue.FromObject(new Dictionary<string, object> { ["1"] = new { path = "folder/item.png" } }) with
        {
            DataType = new("object", KeyType: new("integer"), ValueType: itemType),
        };
        Assert.Equal("path", PathTypeMetadata.TypeOf(dictionary)!.Properties!["1"].Properties!["path"].Type);
        NodeValue iteration = DictionaryValues.ForEach(dictionary);
        Assert.Equal("path", iteration.DataType!.ElementType!.Properties!["1"].Properties!["path"].Type);
        foreach (IActionNode node in new IActionNode[] { new PickNode(), new RandomPickNode() })
        {
            NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(value: dictionary, extra: new Dictionary<string, NodeValue> { ["index"] = NodeValue.FromObject(0) })).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            NodeValue selected = result.Outputs["value"];
            Assert.True(JsonElement.DeepEquals(dictionary.Json, selected.Json));
            Assert.Equal("integer", selected.DataType!.KeyType!.Type);
            Assert.Equal("path", selected.DataType.ValueType!.Properties!["path"].Type);
            Assert.Equal(1, KeyValueParts.Read(selected, true).Json.GetInt32());
            Assert.Equal("path", KeyValueParts.Read(selected, false).DataType!.Properties!["path"].Type);
        }
    }

    /// <summary>素のJSON辞書でもnullや複合値を保持し、候補にない組を生成しません。</summary>
    /// <returns>各選択結果のキーと値が元の同じ項目に属する確認です。</returns>
    [Fact]
    public async Task RandomPickKeepsEachKeyAttachedToItsValueAsync()
    {
        NodeValue dictionary = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("{\"a\":null,\"b/c~d\":false,\"nested\":{\"path\":[1,2]}}"));
        for (int attempt = 0; attempt < 64; attempt++)
        {
            NodeResult result = await new RandomPickNode().ExecuteAsync(NodeTestContext.Create(value: dictionary)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            JsonProperty pair = Assert.Single(result.Outputs["value"].Json.EnumerateObject());
            Assert.True(JsonElement.DeepEquals(dictionary.Json.GetProperty(pair.Name), pair.Value));
        }
        NodeResult first = await new PickNode().ExecuteAsync(NodeTestContext.Create(value: dictionary, extra: new Dictionary<string, NodeValue> { ["index"] = NodeValue.FromObject(0) })).ConfigureAwait(true);
        Assert.Equal(JsonValueKind.Null, first.Outputs["value"].Json.GetProperty("a").ValueKind);
        NodeResult outside = await new PickNode().ExecuteAsync(NodeTestContext.Create(value: dictionary, extra: new Dictionary<string, NodeValue> { ["index"] = NodeValue.FromObject(3) })).ConfigureAwait(true);
        Assert.Equal("value.pick.index.range", outside.ReasonCode);
    }

    /// <summary>辞書の受入れでスカラーまで許可せず、選択結果を一組の型として推測します。</summary>
    /// <returns>型検証と実行時の境界が一致する確認です。</returns>
    [Theory]
    [InlineData("data.pick")]
    [InlineData("data.randomPick")]
    [InlineData("data.count")]
    public void ConnectionsAcceptDictionariesButRejectScalars(string nodeType)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NodeValue dictionary = NodeValue.FromObject(new { a = 1 }) with { DataType = new("object", KeyType: new("string"), ValueType: new("integer")) };
        NodeDefinition input = new("source", "value.comment", empty, new Dictionary<string, NodeValue> { ["value"] = dictionary });
        NodeDefinition pick = new("pick", nodeType, empty, new Dictionary<string, NodeValue> { ["index"] = NodeValue.FromObject(0) });
        WireDefinition wire = new("source", "value", "pick", "value");
        WorkflowDefinition document = new("collection", "Collection", [input, pick], [wire]);
        NodeRegistry registry = new(DataNodeRegistration.Create());
        Assert.True(WorkflowConnectionTypes.CanConnect(document, registry.Descriptors, wire));
        ValueTypeDefinition output = new WorkflowTypeInference(document, registry.Descriptors).PortType("pick", "value", true)!;
        bool count = nodeType is "data.count";
        if (count)
        {
            Assert.Equal("integer", output.Type);
        }
        else
        {
            Assert.Equal("string", output.KeyType!.Type);
            Assert.Equal("integer", output.ValueType!.Type);
        }
        foreach (NodeValue scalar in new[] { NodeValue.FromObject(1), NodeValue.FromObject("abc"), NodeValue.FromObject(false) })
        {
            WorkflowDefinition invalid = document with { Nodes = [input with { Inputs = new Dictionary<string, NodeValue> { ["value"] = scalar } }, pick] };
            Assert.False(WorkflowConnectionTypes.CanConnect(invalid, registry.Descriptors, wire));
        }
    }
}
