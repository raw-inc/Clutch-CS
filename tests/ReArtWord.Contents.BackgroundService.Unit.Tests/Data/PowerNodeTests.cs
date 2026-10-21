using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>累乗とルートの数値、次数、定義域および入力境界を確認します。</summary>
public sealed class PowerNodeTests
{
    /// <summary>値演算として必須入力と省略可能な次数を宣言します。</summary>
    [Fact]
    public void DescriptorsDeclareMathValueOperations()
    {
        PowerNode power = new("power");
        PowerNode root = new("root");
        Assert.Equal("math.power", power.Descriptor.Type);
        Assert.Equal("math.root", root.Descriptor.Type);
        Assert.Equal("Math", power.Descriptor.Category);
        Assert.Equal("Math", root.Descriptor.Category);
        Assert.True(power.Descriptor.IsValueOperation);
        Assert.True(root.Descriptor.IsValueOperation);
        Assert.Equal([new PortDescriptor("base", EPortValueKind.Json, true), new PortDescriptor("exponent", EPortValueKind.Json, true)], power.Descriptor.Inputs);
        Assert.Equal([new PortDescriptor("value", EPortValueKind.Json, true), new PortDescriptor("degree", EPortValueKind.Json)], root.Descriptor.Inputs);
        Assert.Equal(new PortDescriptor("value", EPortValueKind.Json), Assert.Single(power.Descriptor.Outputs));
        Assert.Equal(new PortDescriptor("value", EPortValueKind.Json), Assert.Single(root.Descriptor.Outputs));
    }

    /// <summary>整数・小数・負指数とゼロの累乗をJSON数値で返します。</summary>
    /// <returns>標準累乗と公開出力の確認です。</returns>
    [Theory]
    [InlineData(2, 3, 8)]
    [InlineData(2, -3, 0.125)]
    [InlineData(9, 0.5, 3)]
    [InlineData(-2, 3, -8)]
    [InlineData(-2, 4, 16)]
    [InlineData(-2, -3, -0.125)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 3, 0)]
    public async Task PowerReturnsFiniteJsonNumberAsync(double basis, double exponent, double expected)
    {
        Dictionary<string, NodeValue> inputs = new() { ["base"] = NodeValue.FromObject(basis), ["exponent"] = NodeValue.FromObject(exponent) };
        NodeResult result = await new PowerNode("power").ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(JsonValueKind.Number, result.Outputs["value"].Json.ValueKind);
        Assert.Equal(expected, result.Outputs["value"].Json.GetDouble(), 12);
    }

    /// <summary>既定平方根、奇数次の負数および指数表記の次数を扱います。</summary>
    /// <returns>既定値と正確な整数次数の受入確認です。</returns>
    [Theory]
    [InlineData("9", null, 3)]
    [InlineData("0", null, 0)]
    [InlineData("-27", "3", -3)]
    [InlineData("16", "4", 2)]
    [InlineData("0.0625", "2", 0.25)]
    [InlineData("-7", "1", -7)]
    [InlineData("27", "3.0", 3)]
    [InlineData("27", "30e-1", 3)]
    [InlineData("1", "2147483647", 1)]
    [InlineData("-1", "2147483647", -1)]
    public async Task RootAcceptsDefaultAndPositiveIntegerDegreesAsync(string value, string? degree, double expected)
    {
        Dictionary<string, NodeValue> inputs = new() { ["value"] = NodeValue.FromJson(NodeSettings.Schema(value)) };
        if (degree is not null)
        {
            inputs["degree"] = NodeValue.FromJson(NodeSettings.Schema(degree));
        }
        NodeResult result = await new PowerNode("root").ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(JsonValueKind.Number, result.Outputs["value"].Json.ValueKind);
        Assert.Equal(expected, result.Outputs["value"].Json.GetDouble(), 12);
    }

    /// <summary>数値入力の両端でも恒等演算は範囲エラーにしません。</summary>
    /// <returns>最大有限値と最小subnormalの保存確認です。</returns>
    [Theory]
    [InlineData("power", "base", "exponent", double.MaxValue)]
    [InlineData("power", "base", "exponent", double.Epsilon)]
    [InlineData("root", "value", "degree", double.MaxValue)]
    [InlineData("root", "value", "degree", double.Epsilon)]
    public async Task IdentityPreservesFiniteExtremesAsync(string operation, string valuePort, string secondPort, double expected)
    {
        Dictionary<string, NodeValue> inputs = new() { [valuePort] = NodeValue.FromObject(expected), [secondPort] = NodeValue.FromObject(1) };
        NodeResult result = await new PowerNode(operation).ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].Json.GetDouble());
    }

    /// <summary>不正型、非有限入力、定義域違反と結果の範囲逸脱を失敗にします。</summary>
    /// <returns>失敗理由と出力がないことの確認です。</returns>
    [Theory]
    [InlineData("\"2\"", "3", "math.input.number.type")]
    [InlineData("2", "\"3\"", "math.input.number.type")]
    [InlineData("null", "3", "math.input.number.type")]
    [InlineData("2", "true", "math.input.number.type")]
    [InlineData("1e999", "0", "math.input.number.range")]
    [InlineData("1", "1e999", "math.input.number.range")]
    [InlineData("1e-999", "1", "math.input.number.range")]
    [InlineData("0", "-1", "math.power.domain")]
    [InlineData("-0", "-2", "math.power.domain")]
    [InlineData("-2", "0.5", "math.power.domain")]
    [InlineData("1e308", "2", "math.result.range")]
    [InlineData("1e-300", "2", "math.result.range")]
    [InlineData("2", "-2000", "math.result.range")]
    public async Task InvalidPowerProducesNoValueAsync(string basis, string exponent, string reason)
    {
        Dictionary<string, NodeValue> inputs = new() { ["base"] = NodeValue.FromJson(NodeSettings.Schema(basis)), ["exponent"] = NodeValue.FromJson(NodeSettings.Schema(exponent)) };
        NodeResult result = await new PowerNode("power").ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>次数を丸めずに検証し、負数の偶数次ルートと型不正を拒否します。</summary>
    /// <returns>小数・範囲外・未変換入力の失敗確認です。</returns>
    [Theory]
    [InlineData("9", "0", "math.root.degree")]
    [InlineData("9", "-2", "math.root.degree")]
    [InlineData("9", "2.5", "math.root.degree")]
    [InlineData("9", "2.0000000000000000000001", "math.root.degree")]
    [InlineData("9", "2147483647.0000000001", "math.root.degree")]
    [InlineData("9", "2147483648", "math.root.degree")]
    [InlineData("9", "9223372036854775808", "math.root.degree")]
    [InlineData("9", "1e100", "math.root.degree")]
    [InlineData("-9", "2", "math.root.domain")]
    [InlineData("9", "\"2\"", "math.input.number.type")]
    [InlineData("9", "null", "math.input.number.type")]
    [InlineData("9", "1e999", "math.input.number.range")]
    [InlineData("9", "1e-999", "math.input.number.range")]
    [InlineData("\"9\"", "2", "math.input.number.type")]
    [InlineData("null", "2", "math.input.number.type")]
    [InlineData("1e999", "2", "math.input.number.range")]
    [InlineData("1e-999", "2", "math.input.number.range")]
    public async Task InvalidRootProducesNoValueAsync(string value, string degree, string reason)
    {
        Dictionary<string, NodeValue> inputs = new() { ["value"] = NodeValue.FromJson(NodeSettings.Schema(value)), ["degree"] = NodeValue.FromJson(NodeSettings.Schema(degree)) };
        NodeResult result = await new PowerNode("root").ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>必須入力が欠けている場合は演算しません。</summary>
    /// <returns>欠落入力の失敗確認です。</returns>
    [Theory]
    [InlineData("power", "base")]
    [InlineData("power", "exponent")]
    [InlineData("root", "degree")]
    public async Task MissingRequiredInputProducesNoValueAsync(string operation, string suppliedPort)
    {
        Dictionary<string, NodeValue> inputs = new() { [suppliedPort] = NodeValue.FromObject(2) };
        NodeResult result = await new PowerNode(operation).ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("math.input.missing", result.ReasonCode);
        Assert.Empty(result.Outputs);
    }
}
