using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>配列の空・null要素・入れ子・不正入力と、既存配列ノードとの接続を確認します。</summary>
public sealed class CountNodeTests
{
    /// <summary>直下の要素だけを数え、null・重複・複合値を除外しません。</summary>
    /// <returns>公開ノードの実行結果です。</returns>
    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[null]", 1)]
    [InlineData("[1,1,1]", 3)]
    [InlineData("[null,false,0,\"\",{},[]]", 6)]
    [InlineData("[[1,2],[3]]", 2)]
    [InlineData("{}", 0)]
    [InlineData("{\"a\":null,\"b\":[1,2],\"c\":{\"d\":3}}", 3)]
    public async Task CountsOnlyImmediateItemsAsync(string text, int expected)
    {
        CountNode node = new();
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(text)))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].Json.GetInt32());
    }

    /// <summary>非配列を空配列や一要素へ暗黙変換しません。</summary>
    /// <returns>誤入力の失敗と未出力の確認です。</returns>
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"abc\"")]
    public async Task RejectsNonArraysAsync(string text)
    {
        NodeResult result = await new CountNode().ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(text)))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("value.count.input.collection", result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>バイト列と未供給も配列として数えません。</summary>
    /// <returns>JSON配列との区別です。</returns>
    [Fact]
    public async Task RejectsBytesAndMissingValuesAsync()
    {
        CountNode node = new();
        NodeResult binary = await node.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromBytes(new byte[] { 1, 2 }))).ConfigureAwait(true);
        NodeResult missing = await node.ExecuteAsync(NodeTestContext.Create()).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, binary.Outcome);
        Assert.Equal(EOutcome.Failed, missing.Outcome);
        Assert.Empty(binary.Outputs);
        Assert.Empty(missing.Outputs);
    }

    /// <summary>配列ノードから受け取り、カタログ・型推論・純粋評価でも整数になります。</summary>
    /// <returns>既存値ノードとの互換性です。</returns>
    [Fact]
    public async Task ExistingArraysAndInferredPortsUseTheSameIntegerContractAsync()
    {
        NodeRegistry registry = new(DataNodeRegistration.Create());
        string type = "data.count";
        NodeDescriptor descriptor = registry.Descriptors.Single(node => string.Equals(node.Type, type, StringComparison.Ordinal));
        NodeResult array = await new ArrayValueNode().ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue> { ["items"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("[42]")), ["item"] = NodeValue.Null })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, array.Outcome);
        NodeDefinition definition = new("count", type, JsonSerializer.SerializeToElement(new { }));
        NodeResult result = new CountNode().Evaluate(definition, new Dictionary<string, NodeValue> { ["value"] = array.Outputs["value"] });
        Assert.Equal(2, result.Outputs["value"].Json.GetInt32());
        WorkflowTypeInference inference = new(new("flow", "Count", [definition], []), [descriptor]);
        Assert.Null(inference.PortType("count", "value", false));
        Assert.Equal("integer", inference.PortType("count", "value", true)!.Type);
        Assert.True(descriptor.CanEvaluateStatically);
    }
}
