using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Comparison;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>真偽値を返す比較と、既存条件分岐が同じ値を判定することを確認します。</summary>
public sealed class ComparisonNodeTests
{
    /// <summary>大小関係は整数・decimalの隣接値や指数表記でも丸めません。</summary>
    /// <returns>新規比較と条件分岐の一致です。</returns>
    [Theory]
    [InlineData("less", "2", "3", true)]
    [InlineData("less", "3", "2", false)]
    [InlineData("less", "1", "1.0", false)]
    [InlineData("lessOrEqual", "1", "1.0", true)]
    [InlineData("greater", "-2", "-3", true)]
    [InlineData("greaterOrEqual", "-2", "-2", true)]
    [InlineData("greaterOrEqual", "-3", "-2", false)]
    [InlineData("less", "-1", "0", true)]
    [InlineData("less", "-0", "1e-300", true)]
    [InlineData("lessOrEqual", "-0", "0e100", true)]
    [InlineData("less", "9007199254740992", "9007199254740993", true)]
    [InlineData("less", "0.1234567890123456789012345678", "0.1234567890123456789012345679", true)]
    [InlineData("less", "1e100", "2e100", true)]
    [InlineData("greater", "1e20000", "999e19997", true)]
    [InlineData("less", "-2e100", "-1e100", true)]
    [InlineData("greaterOrEqual", "12e-2", "0.1200", true)]
    public async Task NumericComparisonPreservesPrecisionAsync(string operation, string left, string right, bool expected)
    {
        string settings = "{\"operation\":\"" + operation + "\"}";
        NodeValue first = Value(left);
        NodeValue second = Value(right);
        NodeResult actual = await new ComparisonNode("compare").ExecuteAsync(NodeTestContext.Create(settings, extra: new Dictionary<string, NodeValue> { ["left"] = first, ["right"] = second })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, actual.Outcome);
        Assert.Equal(expected, actual.Outputs["value"].Json.GetBoolean());
        NodeResult branch = await new ConditionNode().ExecuteAsync(NodeTestContext.Create(settings, NodeValue.Null, new Dictionary<string, NodeValue> { ["comparison"] = first, ["expected"] = second })).ConfigureAwait(true);
        Assert.Equal(expected, branch.Outputs["result"].Json.GetBoolean());
    }

    /// <summary>型・構造・配列順序の判定が一致と不一致で反転します。</summary>
    /// <returns>両ノードの真偽値です。</returns>
    [Theory]
    [InlineData("null", "null", true)]
    [InlineData("1", "1.0", true)]
    [InlineData("1e2", "100", true)]
    [InlineData("\"1\"", "1", false)]
    [InlineData("true", "1", false)]
    [InlineData("\"A\"", "\"a\"", false)]
    [InlineData("[]", "[]", true)]
    [InlineData("[1,2]", "[2,1]", false)]
    [InlineData("{\"a\":1,\"b\":[2]}", "{\"b\":[2.0],\"a\":1.0}", true)]
    [InlineData("9007199254740992", "9007199254740993", false)]
    [InlineData("0.1234567890123456789012345678", "0.1234567890123456789012345679", false)]
    public async Task EqualityUsesTypedValuesAsync(string left, string right, bool expected)
    {
        await CheckEqualityAsync(Value(left), Value(right), expected).ConfigureAwait(true);
    }

    /// <summary>バイト列は同じバイト列だけと等しく、JSON配列へ変換しません。</summary>
    /// <returns>バイナリの判定です。</returns>
    [Fact]
    public async Task EqualityPreservesBinaryKindAsync()
    {
        NodeValue bytes = NodeValue.FromBytes(new byte[] { 1, 2 });
        await CheckEqualityAsync(bytes, NodeValue.FromBytes(new byte[] { 1, 2 }), true).ConfigureAwait(true);
        await CheckEqualityAsync(bytes, NodeValue.FromBytes(new byte[] { 2, 1 }), false).ConfigureAwait(true);
        await CheckEqualityAsync(bytes, Value("[1,2]"), false).ConfigureAwait(true);
    }

    /// <summary>不正な大小比較を正常なfalseとして返しません。</summary>
    /// <returns>比較不能を表示する失敗です。</returns>
    [Theory]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1e2147483648")]
    public async Task InvalidOrderingHasNoBooleanOutputAsync(string left)
    {
        NodeResult result = await new ComparisonNode("compare").ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue> { ["left"] = Value(left), ["right"] = Value("1") })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>省略した比較条件は小なりになり、未定義の条件を黙って採用しません。</summary>
    /// <returns>既定と検証エラーの確認です。</returns>
    [Fact]
    public async Task DefaultRelationAndInvalidSelectionAreDistinctAsync()
    {
        Dictionary<string, NodeValue> inputs = new() { ["left"] = Value("2"), ["right"] = Value("3") };
        NodeResult result = await new ComparisonNode("compare").ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        Assert.True(result.Outputs["value"].Json.GetBoolean());
        NodeResult invalid = await new ComparisonNode("compare").ExecuteAsync(NodeTestContext.Create("""{"operation":"removed-option"}""", extra: inputs)).ConfigureAwait(true);
        Assert.NotEqual(EOutcome.Succeeded, invalid.Outcome);
        Assert.Empty(invalid.Outputs);
    }

    /// <summary>未入力と明示nullを同一視しません。</summary>
    /// <returns>必須入力の拒否です。</returns>
    [Theory]
    [InlineData("compare")]
    [InlineData("equal")]
    [InlineData("notEqual")]
    public async Task MissingInputIsNotAComparisonAsync(string operation)
    {
        NodeResult result = await new ComparisonNode(operation).ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue> { ["left"] = NodeValue.Null })).ConfigureAwait(true);
        Assert.NotEqual(EOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>比較対象のJSONを保持します。</summary>
    /// <returns>型を変更しない入力です。</returns>
    private static NodeValue Value(string json) => NodeValue.FromJson(NodeSettings.Schema(json));

    /// <summary>一致と不一致の結果を対にして検証します。</summary>
    /// <returns>値判定の確認です。</returns>
    private static async Task CheckEqualityAsync(NodeValue left, NodeValue right, bool expected)
    {
        Dictionary<string, NodeValue> inputs = new() { ["left"] = left, ["right"] = right };
        NodeResult equal = await new ComparisonNode("equal").ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        NodeResult different = await new ComparisonNode("notEqual").ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, equal.Outcome);
        Assert.Equal(EOutcome.Succeeded, different.Outcome);
        Assert.Equal(expected, equal.Outputs["value"].Json.GetBoolean());
        Assert.Equal(!expected, different.Outputs["value"].Json.GetBoolean());
    }
}
