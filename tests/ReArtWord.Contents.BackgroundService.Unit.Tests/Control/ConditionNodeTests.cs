using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>条件結果と未取得値の境界を検証します。</summary>
public sealed class ConditionNodeTests
{
    /// <summary>比較値がなければ判定不能であり偽や実行不明にはしません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task UnavailableComparisonIsNormalIndeterminate()
    {
        NodeResult result = await new ConditionNode().ExecuteAsync(NodeTestContext.Create("""{"operation":"equal","expected":0}""", NodeValue.FromObject("original")));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("indeterminate", result.Route);
        Assert.False(result.Outputs.ContainsKey("comparison"));
        Assert.Equal("original", result.Outputs["value"].GetString());
        Assert.Equal("condition.value.unavailable", result.Outputs["reason"].Json.GetProperty("kind").GetString());
    }

    /// <summary>一致・不一致のどちらでも元入力を保ちます。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData(5, "true")]
    [InlineData(7, "false")]
    public async Task ComparisonRoutesOriginalValue(int actual, string route)
    {
        NodeResult result = await new ConditionNode().ExecuteAsync(NodeTestContext.Create("""{"operation":"equal","expected":5}""", NodeValue.FromObject("payload"), new Dictionary<string, NodeValue> { ["comparison"] = NodeValue.FromObject(actual) }));
        Assert.Equal(route, result.Route);
        Assert.Equal("payload", result.Outputs["value"].GetString());
        Assert.Equal(actual, result.Outputs["comparison"].Json.GetInt32());
    }

    /// <summary>型不一致は判定不能として理由を返します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task OrderedComparisonDoesNotCoerceTextToNumber()
    {
        NodeResult result = await new ConditionNode().ExecuteAsync(NodeTestContext.Create("""{"operation":"less","expected":5}""", NodeValue.Null, new Dictionary<string, NodeValue> { ["comparison"] = NodeValue.FromObject("3") }));
        Assert.Equal("indeterminate", result.Route);
        Assert.Equal("condition.value.incompatible", result.Outputs["reason"].Json.GetProperty("kind").GetString());
    }
}
