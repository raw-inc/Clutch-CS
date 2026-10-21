using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>共通四則実装の演算順・型・数値境界を確認します。</summary>
public sealed class ArithmeticNodeTests
{
    /// <summary>左右の順序を維持し、演算ごとの数値を返します。</summary>
    /// <returns>演算と宣言型の一致です。</returns>
    [Theory]
    [InlineData("add", 9, 4, 13)]
    [InlineData("subtract", 9, 4, 5)]
    [InlineData("multiply", 9, 4, 36)]
    [InlineData("divide", 9, 4, 2.25)]
    public async Task FourOperationsReturnJsonNumbersAsync(string operation, double left, double right, double expected)
    {
        ArithmeticNode node = new(operation);
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue> { ["left"] = NodeValue.FromObject(left), ["right"] = NodeValue.FromObject(right) })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].Json.GetDouble());
        Assert.Equal(EPortValueKind.Json, node.Descriptor.Outputs.Single().ValueKind);
        Assert.Equal("Math", node.Descriptor.Category);
        Assert.Single(ArithmeticNodeRegistration.Create(), registered => string.Equals(registered.Descriptor.Type, node.Descriptor.Type, StringComparison.Ordinal));
    }

    /// <summary>文字列・ゼロ除算・overflow・underflowを暗黙変換しません。</summary>
    /// <returns>具体的な失敗の確認です。</returns>
    [Theory]
    [InlineData("add", "\"1\"", "2", "math.input.number.type")]
    [InlineData("add", "null", "2", "math.input.number.type")]
    [InlineData("divide", "1", "0", "math.divide.zero")]
    [InlineData("divide", "1", "-0", "math.divide.zero")]
    [InlineData("multiply", "1e308", "2", "math.result.range")]
    [InlineData("multiply", "1e-300", "1e-300", "math.result.range")]
    [InlineData("add", "1e999", "2", "math.input.number.range")]
    public async Task InvalidArithmeticProducesNoValueAsync(string operation, string left, string right, string reason)
    {
        Dictionary<string, NodeValue> input = new() { ["left"] = NodeValue.FromJson(NodeSettings.Schema(left)), ["right"] = NodeValue.FromJson(NodeSettings.Schema(right)) };
        NodeResult result = await new ArithmeticNode(operation).ExecuteAsync(NodeTestContext.Create(extra: input)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Outputs);
    }
}
