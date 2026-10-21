using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>整数の両端・Int64全域と、小数の有限範囲・上限除外を検証します。</summary>
public sealed class RandomNodeTests
{
    /// <summary>整数は丸めず、符号やInt64端でも指定範囲に収まります。</summary>
    /// <returns>オーバーフローも範囲外もない抽選です。</returns>
    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(1L, 6L)]
    [InlineData(-8L, -2L)]
    [InlineData(-10L, 10L)]
    [InlineData(long.MinValue, long.MaxValue)]
    [InlineData(long.MinValue, long.MinValue + 1)]
    [InlineData(long.MaxValue - 1, long.MaxValue)]
    [InlineData(long.MinValue, long.MinValue)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public async Task IntegerBoundsAreInclusiveAcrossInt64Async(long minimum, long maximum)
    {
        string settings = JsonSerializer.Serialize(new { minimum, maximum });
        for (int attempt = 0; attempt < 64; attempt++)
        {
            NodeResult result = await new RandomNode().ExecuteAsync(NodeTestContext.Create(settings)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.InRange(result.Outputs["value"].Json.GetInt64(), minimum, maximum);
        }
    }

    /// <summary>小数は巨大範囲でも有限で、丸めにより上端へはみ出しません。</summary>
    /// <returns>公開した半開区間に含まれる数値です。</returns>
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(-9.75, -2.5)]
    [InlineData(-double.MaxValue, double.MaxValue)]
    [InlineData(0.0, double.Epsilon)]
    [InlineData(-double.Epsilon, 0.0)]
    [InlineData(1.0, 1.0000000000000002)]
    public async Task DecimalsStayBelowTheUpperBoundAsync(double minimum, double maximum)
    {
        string settings = JsonSerializer.Serialize(new { minimum, maximum, mode = "decimal" });
        for (int attempt = 0; attempt < 64; attempt++)
        {
            NodeResult result = await new RandomNode().ExecuteAsync(NodeTestContext.Create(settings)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            double value = result.Outputs["value"].Json.GetDouble();
            Assert.True(double.IsFinite(value));
            Assert.InRange(value, minimum, Math.BitDecrement(maximum));
        }
    }

    /// <summary>等しい上下限は、整数・小数のどちらもその値を返します。</summary>
    /// <returns>一つだけの候補の結果です。</returns>
    [Theory]
    [InlineData("{\"minimum\":-2.75,\"maximum\":-2.75,\"mode\":\"decimal\"}", -2.75)]
    [InlineData("{\"minimum\":2e0,\"maximum\":2.0}", 2.0)]
    public async Task EqualBoundsReturnTheirOnlyValueAsync(string settings, double expected)
    {
        NodeResult result = await new RandomNode().ExecuteAsync(NodeTestContext.Create(settings)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].Json.GetDouble());
    }

    /// <summary>不正な区間・非有限・整数の精度損失を抽選前に拒否します。</summary>
    /// <returns>値を持たない設定エラーです。</returns>
    [Theory]
    [InlineData("{\"minimum\":2,\"maximum\":1}")]
    [InlineData("{\"minimum\":2,\"maximum\":1,\"mode\":\"decimal\"}")]
    [InlineData("{\"minimum\":0.1}")]
    [InlineData("{\"maximum\":9223372036854775808}")]
    [InlineData("{\"minimum\":-9223372036854775809}")]
    [InlineData("{\"minimum\":1e1000,\"mode\":\"decimal\"}")]
    [InlineData("{\"minimum\":1e-1000,\"mode\":\"decimal\"}")]
    [InlineData("{\"minimum\":\"0\"}")]
    [InlineData("{\"maximum\":null}")]
    [InlineData("{\"mode\":\"unknown\"}")]
    public async Task InvalidBoundsDoNotProduceValuesAsync(string settings)
    {
        NodeResult result = await new RandomNode().ExecuteAsync(NodeTestContext.Create(settings)).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>既定区間と、抽選を伴うため静的評価しない契約を確認します。</summary>
    /// <returns>通常登録の公開動作です。</returns>
    [Fact]
    public async Task DefaultsToZeroOrOneAndCannotEvaluateStaticallyAsync()
    {
        NodeResult result = await new RandomNode().ExecuteAsync(NodeTestContext.Create()).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.InRange(result.Outputs["value"].Json.GetInt64(), 0, 1);
        NodeRegistry registry = new(ArithmeticNodeRegistration.Create());
        Assert.False(registry.Descriptors.Single(node => node.Type is "math.random").CanEvaluateStatically);
    }
}
