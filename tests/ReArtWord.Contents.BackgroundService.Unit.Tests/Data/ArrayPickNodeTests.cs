using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>選択要素の値・型と、空配列や不正Indexの境界を検証します。</summary>
public sealed class ArrayPickNodeTests
{
    /// <summary>文字列・数値・複合値・nullを別の型へ変換せず選択します。</summary>
    /// <returns>Indexに対応する一つの値です。</returns>
    [Theory]
    [InlineData("0", "null")]
    [InlineData("1", "false")]
    [InlineData("2.0", "42")]
    [InlineData("3e0", "\"001\"")]
    [InlineData("4", "{\"key\":7}")]
    [InlineData("5", "[1,2]")]
    public async Task PicksTheExactZeroBasedItemAsync(string index, string expected)
    {
        NodeValue array = Json("[null,false,42,\"001\",{\"key\":7},[1,2]]");
        NodeResult result = await new PickNode().ExecuteAsync(NodeTestContext.Create(value: array, extra: new Dictionary<string, NodeValue> { ["index"] = Json(index) })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(JsonElement.DeepEquals(NodeSettings.Schema(expected), result.Outputs["value"].Json));
        Assert.Equal(6, array.Json.GetArrayLength());
    }

    /// <summary>不存在を正常nullへ置き換えず、小数・文字列も丸めません。</summary>
    /// <returns>失敗し、値を出力しないことの確認です。</returns>
    [Theory]
    [InlineData("-1")]
    [InlineData("1")]
    [InlineData("0.1")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"0\"")]
    [InlineData("9223372036854775808")]
    [InlineData("1e1000")]
    public async Task RejectsInvalidIndicesAsync(string index)
    {
        NodeResult result = await new PickNode().ExecuteAsync(NodeTestContext.Create(value: Json("[null]"), extra: new Dictionary<string, NodeValue> { ["index"] = Json(index) })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>両選択ノードは空配列と非配列を同じ境界で拒否します。</summary>
    /// <returns>配列を暗黙作成しない確認です。</returns>
    [Theory]
    [InlineData("[]", "value.pick.collection.empty")]
    [InlineData("null", "value.pick.input.collection")]
    [InlineData("{}", "value.pick.collection.empty")]
    [InlineData("\"abc\"", "value.pick.input.collection")]
    [InlineData("42", "value.pick.input.collection")]
    public async Task EmptyAndNonArrayInputsFailAsync(string array, string reason)
    {
        foreach (IActionNode node in new IActionNode[] { new PickNode(), new RandomPickNode() })
        {
            NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(value: Json(array), extra: new Dictionary<string, NodeValue> { ["index"] = NodeValue.FromObject(0) })).ConfigureAwait(true);
            Assert.Equal(EOutcome.Failed, result.Outcome);
            Assert.Equal(reason, result.ReasonCode);
            Assert.Empty(result.Outputs);
        }
    }

    /// <summary>入力未供給やバイト列を配列の一要素として扱いません。</summary>
    /// <returns>入力種別の区別です。</returns>
    [Fact]
    public async Task MissingAndBinaryInputsFailAsync()
    {
        foreach (IActionNode node in new IActionNode[] { new PickNode(), new RandomPickNode() })
        {
            NodeResult missing = await node.ExecuteAsync(NodeTestContext.Create()).ConfigureAwait(true);
            NodeResult binary = await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromBytes(new byte[] { 1 }))).ConfigureAwait(true);
            Assert.Equal(EOutcome.Failed, missing.Outcome);
            Assert.Equal(EOutcome.Failed, binary.Outcome);
        }
        NodeResult noIndex = await new PickNode().ExecuteAsync(NodeTestContext.Create(value: Json("[1]"))).ConfigureAwait(true);
        Assert.Equal("value.pick.index.integer", noIndex.ReasonCode);
    }

    /// <summary>Pathを含む複合要素の明示型は両選択ノードで維持します。</summary>
    /// <returns>値本文と所有型の確認です。</returns>
    [Fact]
    public async Task BothSelectionsPreserveNestedPathMetadataAsync()
    {
        ValueTypeDefinition element = new("object", Properties: new Dictionary<string, ValueTypeDefinition> { ["path"] = new("path") });
        NodeValue array = Json("[{\"path\":\"C:/content/file.txt\"}]") with { DataType = new("array", element) };
        foreach (IActionNode node in new IActionNode[] { new PickNode(), new RandomPickNode() })
        {
            NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(value: array, extra: new Dictionary<string, NodeValue> { ["index"] = NodeValue.FromObject(0) })).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal("C:/content/file.txt", result.Outputs["value"].Json.GetProperty("path").GetString());
            Assert.Equal("path", result.Outputs["value"].DataType!.Properties!["path"].Type);
            Assert.NotSame(element.Properties, result.Outputs["value"].DataType!.Properties);
        }
    }

    /// <summary>候補にない値を生成せず、存在するnullや一要素の配列も抽選できます。</summary>
    /// <returns>元の候補からの選択です。</returns>
    [Fact]
    public async Task RandomPickReturnsAnOriginalCandidateAsync()
    {
        NodeValue array = Json("[null,false,\"001\",{\"id\":3},[4,5],\"001\"]");
        RandomPickNode node = new();
        for (int attempt = 0; attempt < 128; attempt++)
        {
            NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(value: array)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Contains(array.Json.EnumerateArray(), item => JsonElement.DeepEquals(item, result.Outputs["value"].Json));
        }
        NodeResult only = await node.ExecuteAsync(NodeTestContext.Create(value: Json("[null]"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, only.Outcome);
        Assert.Equal(JsonValueKind.Null, only.Outputs["value"].Json.ValueKind);
        NodeRegistry registry = new(DataNodeRegistration.Create());
        Assert.True(registry.Descriptors.Single(item => item.Type is "data.pick").CanEvaluateStatically);
        Assert.False(registry.Descriptors.Single(item => item.Type is "data.randomPick").CanEvaluateStatically);
    }

    /// <summary>文書寿命から独立した入力を作ります。</summary>
    /// <returns>試験用のJSON値です。</returns>
    private static NodeValue Json(string value) => NodeValue.FromJson(NodeSettings.Schema(value));
}
