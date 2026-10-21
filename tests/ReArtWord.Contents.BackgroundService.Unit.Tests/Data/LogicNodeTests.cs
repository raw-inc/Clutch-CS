using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Logic;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>単項・二項・配列論理演算の真理値、整数ビットおよび入力境界を確認します。</summary>
public sealed class LogicNodeTests
{
    /// <summary>二項論理演算の全真理値表をJSON真偽値として返します。</summary>
    /// <returns>五演算それぞれ四通りの真理値表です。</returns>
    [Theory]
    [InlineData("and", false, false, false)]
    [InlineData("and", false, true, false)]
    [InlineData("and", true, false, false)]
    [InlineData("and", true, true, true)]
    [InlineData("or", false, false, false)]
    [InlineData("or", false, true, true)]
    [InlineData("or", true, false, true)]
    [InlineData("or", true, true, true)]
    [InlineData("nand", false, false, true)]
    [InlineData("nand", false, true, true)]
    [InlineData("nand", true, false, true)]
    [InlineData("nand", true, true, false)]
    [InlineData("nor", false, false, true)]
    [InlineData("nor", false, true, false)]
    [InlineData("nor", true, false, false)]
    [InlineData("nor", true, true, false)]
    [InlineData("xor", false, false, false)]
    [InlineData("xor", false, true, true)]
    [InlineData("xor", true, false, true)]
    [InlineData("xor", true, true, false)]
    public async Task BinaryBooleanTruthTablesAsync(string operation, bool left, bool right, bool expected)
    {
        Dictionary<string, NodeValue> inputs = new() { ["left"] = NodeValue.FromObject(left), ["right"] = NodeValue.FromObject(right) };
        NodeResult result = await new LogicNode(operation).ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        AssertBoolean(result, expected);
    }

    /// <summary>NOTと配列演算の真偽値を計算します。</summary>
    /// <returns>単項反転と配列全体の論理結果です。</returns>
    [Theory]
    [InlineData("not", "true", false)]
    [InlineData("not", "false", true)]
    [InlineData("ands", "[true,true]", true)]
    [InlineData("ands", "[true,true,true]", true)]
    [InlineData("ands", "[true,true,false]", false)]
    [InlineData("ors", "[false,false]", false)]
    [InlineData("ors", "[false,false,false]", false)]
    [InlineData("ors", "[false,false,true]", true)]
    public async Task UnaryAndArrayBooleanOperationsAsync(string operation, string value, bool expected)
    {
        NodeResult result = await RunAsync(operation, value).ConfigureAwait(true);
        AssertBoolean(result, expected);
    }

    /// <summary>正負のビット列とInt64両端を丸めずに計算します。</summary>
    /// <returns>JSON整数として正確なビット演算結果です。</returns>
    [Theory]
    [InlineData("and", "12", "10", 8L)]
    [InlineData("or", "12", "10", 14L)]
    [InlineData("xor", "12", "10", 6L)]
    [InlineData("nand", "12", "10", -9L)]
    [InlineData("nor", "12", "10", -15L)]
    [InlineData("and", "-2", "7", 6L)]
    [InlineData("or", "-2", "1", -1L)]
    [InlineData("xor", "-1", "1", -2L)]
    [InlineData("and", "9223372036854775807", "-1", long.MaxValue)]
    [InlineData("or", "-9223372036854775808", "0", long.MinValue)]
    [InlineData("xor", "9223372036854775807", "-9223372036854775808", -1L)]
    [InlineData("and", "9007199254740993", "1", 1L)]
    [InlineData("or", "3.0", "40e-1", 7L)]
    public async Task BinaryIntegerOperationsPreserveBitsAsync(string operation, string left, string right, long expected)
    {
        NodeResult result = await RunAsync(operation, left, right).ConfigureAwait(true);
        AssertInteger(result, expected);
    }

    /// <summary>NOTの全ビット反転と配列全体の整数演算を確認します。</summary>
    /// <returns>単項と多項の正確な整数結果です。</returns>
    [Theory]
    [InlineData("not", "0", -1L)]
    [InlineData("not", "1", -2L)]
    [InlineData("not", "-1", 0L)]
    [InlineData("not", "9223372036854775807", long.MinValue)]
    [InlineData("not", "-9223372036854775808", long.MaxValue)]
    [InlineData("ands", "[15,12,10]", 8L)]
    [InlineData("ands", "[-1,9223372036854775807,9007199254740993]", 9007199254740993L)]
    [InlineData("ors", "[1,2,4]", 7L)]
    [InlineData("ors", "[-9223372036854775808,0,1]", -9223372036854775807L)]
    public async Task UnaryAndArrayIntegerOperationsAsync(string operation, string value, long expected)
    {
        NodeResult result = await RunAsync(operation, value).ConfigureAwait(true);
        AssertInteger(result, expected);
    }

    /// <summary>二項演算は型混在、不正型、小数、範囲外整数を受理しません。</summary>
    /// <returns>左右両側を検証することの確認です。</returns>
    [Theory]
    [InlineData("true", "1", "math.input.logic.type")]
    [InlineData("1", "true", "math.input.logic.type")]
    [InlineData("false", "null", "math.input.logic.type")]
    [InlineData("\"true\"", "true", "math.input.logic.type")]
    [InlineData("1", "\"1\"", "math.input.logic.type")]
    [InlineData("[]", "1", "math.input.logic.type")]
    [InlineData("true", "{}", "math.input.logic.type")]
    [InlineData("1.5", "1", "math.input.integer.fractional")]
    [InlineData("0", "1.00000000000000000001", "math.input.integer.fractional")]
    [InlineData("1", "9223372036854775808", "math.input.integer.range")]
    [InlineData("-9223372036854775809", "1", "math.input.integer.range")]
    [InlineData("1e999", "1", "math.input.number.range")]
    [InlineData("1", "1e-999", "math.input.number.range")]
    public async Task InvalidBinaryOperandsProduceNoValueAsync(string left, string right, string reason)
    {
        foreach (string operation in new[] { "and", "or", "nand", "nor", "xor" })
        {
            NodeResult result = await RunAsync(operation, left, right).ConfigureAwait(true);
            AssertFailure(result, reason);
        }
    }

    /// <summary>単項・配列の型不正と短絡結果の後にある不正要素を拒否します。</summary>
    /// <returns>配列長、全要素検証および単項数値境界の確認です。</returns>
    [Theory]
    [InlineData("not", "null", "math.input.logic.type")]
    [InlineData("not", "\"1\"", "math.input.logic.type")]
    [InlineData("not", "{}", "math.input.logic.type")]
    [InlineData("not", "[]", "math.input.logic.type")]
    [InlineData("not", "1.1", "math.input.integer.fractional")]
    [InlineData("not", "9223372036854775808", "math.input.integer.range")]
    [InlineData("ands", "true", "math.input.array.type")]
    [InlineData("ors", "null", "math.input.array.type")]
    [InlineData("ands", "[]", "math.input.array.range")]
    [InlineData("ors", "[true]", "math.input.array.range")]
    [InlineData("ands", "[false,true,null]", "math.input.logic.type")]
    [InlineData("ors", "[true,false,1]", "math.input.logic.type")]
    [InlineData("ands", "[0,1,true]", "math.input.logic.type")]
    [InlineData("ors", "[-1,1,\"1\"]", "math.input.logic.type")]
    [InlineData("ands", "[0,1,[]]", "math.input.logic.type")]
    [InlineData("ors", "[-1,1,1.000000000000001]", "math.input.integer.fractional")]
    [InlineData("ands", "[0,1,9223372036854775808]", "math.input.integer.range")]
    public async Task InvalidUnaryOrArrayOperandsProduceNoValueAsync(string operation, string value, string reason)
    {
        NodeResult result = await RunAsync(operation, value).ConfigureAwait(true);
        AssertFailure(result, reason);
    }

    /// <summary>配列長の上限を適用し、上限以内の全要素を受理します。</summary>
    /// <returns>4096要素の受入と4097要素の拒否です。</returns>
    [Theory]
    [InlineData("ands")]
    [InlineData("ors")]
    public async Task ArraysHaveFiniteOperandLimitAsync(string operation)
    {
        NodeValue accepted = NodeValue.FromObject(Enumerable.Repeat(true, 4096).ToArray());
        NodeResult success = await new LogicNode(operation).ExecuteAsync(NodeTestContext.Create(value: accepted)).ConfigureAwait(true);
        AssertBoolean(success, true);
        NodeValue rejected = NodeValue.FromObject(Enumerable.Repeat(true, 4097).ToArray());
        NodeResult failure = await new LogicNode(operation).ExecuteAsync(NodeTestContext.Create(value: rejected)).ConfigureAwait(true);
        AssertFailure(failure, "math.input.array.range");
    }

    /// <summary>宣言した各必須入力の欠落とバイナリを拒否します。</summary>
    /// <returns>全八演算の入力ポート境界です。</returns>
    [Fact]
    public async Task EveryDeclaredInputIsRequiredAndRejectsBinaryAsync()
    {
        foreach (INode registered in LogicNodeRegistration.Create())
        {
            LogicNode node = Assert.IsType<LogicNode>(registered);
            foreach (PortDescriptor port in node.Descriptor.Inputs)
            {
                Dictionary<string, NodeValue> inputs = node.Descriptor.Inputs.ToDictionary(input => input.Name, _ => NodeValue.FromObject(true), StringComparer.Ordinal);
                inputs.Remove(port.Name);
                NodeResult missing = await node.ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
                AssertFailure(missing, "math.input.missing");
                inputs[port.Name] = NodeValue.FromBytes(new byte[] { 1 });
                NodeResult binary = await node.ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
                Assert.Equal(EOutcome.Failed, binary.Outcome);
                Assert.Empty(binary.Outputs);
            }
        }
    }

    /// <summary>八つの演算を公開し、単項・二項・配列入力を区別します。</summary>
    [Fact]
    public void RegistrationDeclaresEightMathValueOperations()
    {
        IReadOnlyList<INode> nodes = LogicNodeRegistration.Create();
        Assert.Equal(["math.and", "math.or", "math.not", "math.nand", "math.nor", "math.xor", "math.ands", "math.ors"], nodes.Select(node => node.Descriptor.Type));
        foreach (INode node in nodes)
        {
            Assert.IsAssignableFrom<IActionNode>(node);
            Assert.Equal("Math", node.Descriptor.Category);
            Assert.True(node.Descriptor.IsValueOperation);
            PortDescriptor[] expected = node.Descriptor.Type switch
            {
                "math.not" => [new("value", EPortValueKind.Any, true)],
                "math.ands" or "math.ors" => [new("value", EPortValueKind.Json, true)],
                _ => [new("left", EPortValueKind.Any, true), new("right", EPortValueKind.Any, true)],
            };
            Assert.Equal(expected, node.Descriptor.Inputs);
            Assert.Equal(new PortDescriptor("value", EPortValueKind.Json), Assert.Single(node.Descriptor.Outputs));
        }
    }

    /// <summary>演算に対応したポートへJSON入力を渡します。</summary>
    /// <returns>ノードの実行結果です。</returns>
    private static async Task<NodeResult> RunAsync(string operation, string value, string? right = null)
    {
        string port = operation switch
        {
            "not" or "ands" or "ors" => "value",
            _ => "left",
        };
        Dictionary<string, NodeValue> inputs = new() { [port] = NodeValue.FromJson(NodeSettings.Schema(value)) };
        if (right is not null)
        {
            inputs["right"] = NodeValue.FromJson(NodeSettings.Schema(right));
        }
        return await new LogicNode(operation).ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
    }

    /// <summary>結果がJSON真偽値であり、期待値と一致することを確認します。</summary>
    private static void AssertBoolean(NodeResult result, bool expected)
    {
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(EValueKind.Json, result.Outputs["value"].Kind);
        Assert.True(result.Outputs["value"].Json.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.Equal(expected, result.Outputs["value"].Json.GetBoolean());
    }

    /// <summary>結果がJSON整数であり、丸めず期待値と一致することを確認します。</summary>
    private static void AssertInteger(NodeResult result, long expected)
    {
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(JsonValueKind.Number, result.Outputs["value"].Json.ValueKind);
        Assert.Equal(expected, result.Outputs["value"].Json.GetInt64());
    }

    /// <summary>失敗理由と出力がないことを確認します。</summary>
    private static void AssertFailure(NodeResult result, string reason)
    {
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Empty(result.Outputs);
    }
}
