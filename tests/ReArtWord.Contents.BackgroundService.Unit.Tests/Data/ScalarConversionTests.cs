using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>明示スカラー変換の型・精度・失敗境界を確認します。</summary>
public sealed class ScalarConversionTests
{
    /// <summary>文字列・数値・整数・真偽値を指定した型へ変換します。</summary>
    /// <returns>実出力の確認です。</returns>
    [Theory]
    [InlineData("string", "42", "\"42\"")]
    [InlineData("string", "true", "\"true\"")]
    [InlineData("string", "\"hello\"", "\"hello\"")]
    [InlineData("number", "\"1.25e2\"", "125")]
    [InlineData("number", "42", "42")]
    [InlineData("integer", "\"9007199254740993\"", "9007199254740993")]
    [InlineData("integer", "9223372036854775807", "9223372036854775807")]
    [InlineData("integer", "\"-9223372036854775808\"", "-9223372036854775808")]
    [InlineData("integer", "\"4.200e1\"", "42")]
    [InlineData("boolean", "true", "true")]
    [InlineData("boolean", "\"FALSE\"", "false")]
    [InlineData("boolean", "1.000", "true")]
    [InlineData("boolean", "0", "false")]
    [InlineData("boolean", "\"1\"", "true")]
    [InlineData("boolean", "\"0\"", "false")]
    public async Task ExplicitConversionsProduceDeclaredJsonAsync(string target, string input, string expected)
    {
        ConvertNode node = new();
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(Settings(target), NodeValue.FromJson(NodeSettings.Schema(input)))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(EPortValueKind.Json, node.Descriptor.Outputs.Single().ValueKind);
        Assert.Equal(EValueKind.Json, result.Outputs["value"].Kind);
        Assert.True(JsonElement.DeepEquals(NodeSettings.Schema(expected), result.Outputs["value"].Json));
    }

    /// <summary>不正・範囲外・小数をゼロや切捨てで成功にしません。</summary>
    /// <returns>理由付き失敗です。</returns>
    [Theory]
    [InlineData("integer", "\"1.0000000000000000000000000000001\"", "data.convert.integer.fractional")]
    [InlineData("integer", "\"9223372036854775808\"", "data.convert.integer.range")]
    [InlineData("integer", "\"-9223372036854775809\"", "data.convert.integer.range")]
    [InlineData("number", "\"NaN\"", "data.convert.number.range")]
    [InlineData("number", "\"Infinity\"", "data.convert.number.range")]
    [InlineData("number", "\"1e999\"", "data.convert.number.range")]
    [InlineData("number", "\"1e-400\"", "data.convert.number.range")]
    [InlineData("number", "\"1,5\"", "data.convert.number.format")]
    [InlineData("number", "true", "data.convert.number.type")]
    [InlineData("boolean", "2", "data.convert.boolean.range")]
    [InlineData("boolean", "1.00000000000000000001", "data.convert.integer.fractional")]
    [InlineData("string", "null", "data.convert.input.type")]
    [InlineData("string", "{}", "data.convert.input.type")]
    [InlineData("number", "[]", "data.convert.input.type")]
    public async Task InvalidConversionsRemainFailedAsync(string target, string input, string reason)
    {
        NodeResult result = await new ConvertNode().ExecuteAsync(NodeTestContext.Create(Settings(target), NodeValue.FromJson(NodeSettings.Schema(input)))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>バイト入力を文字列へ暗黙decodeしません。</summary>
    /// <returns>非JSON入力の拒否です。</returns>
    [Fact]
    public async Task BinaryIsNotImplicitlyDecodedAsync()
    {
        NodeResult result = await new ConvertNode().ExecuteAsync(NodeTestContext.Create(Settings("string"), NodeValue.FromBytes(new byte[] { 65 }))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("data.convert.input.type", result.ReasonCode);
    }

    /// <summary>変換先だけを指定します。</summary>
    /// <returns>公開スキーマに従う設定です。</returns>
    private static string Settings(string target) => JsonSerializer.Serialize(new { targetType = target });
}
