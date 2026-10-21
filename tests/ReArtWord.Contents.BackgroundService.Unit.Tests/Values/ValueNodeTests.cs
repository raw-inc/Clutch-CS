using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Values;

/// <summary>純粋値の精度と合成の境界を、実ノードから検証します。</summary>
public sealed class ValueNodeTests
{
    /// <summary>十進数・整数・文字を暗黙の丸めなしで保持します。</summary>
    [Theory]
    [InlineData("byte", "255", "255")]
    [InlineData("byte", "0", "0")]
    [InlineData("integer", "9223372036854775807", "9223372036854775807")]
    [InlineData("integer", "-9223372036854775808", "-9223372036854775808")]
    [InlineData("integer", "9007199254740993", "9007199254740993")]
    [InlineData("decimal", "79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("decimal", "0.1234567890123456789012345678", "0.1234567890123456789012345678")]
    [InlineData("decimal", "0.0e-2147483648", "0")]
    [InlineData("decimal", "-0.000e-2147483647", "0")]
    [InlineData("decimal", "0e2147483647", "0")]
    [InlineData("number", "1.25e2", "125")]
    [InlineData("boolean", "1", "true")]
    [InlineData("boolean", "false", "false")]
    [InlineData("char", "😀", "\"😀\"")]
    [InlineData("string", "42", "\"42\"")]
    [InlineData("auto", "9007199254740993", "9007199254740993")]
    [InlineData("auto", "hello", "\"hello\"")]
    [InlineData("auto", "[1,true,null]", "[1,true,null]")]
    public void ScalarsPreserveValues(string type, string text, string expected)
    {
        NodeResult result = Constant(type, text);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(JsonElement.DeepEquals(NodeSettings.Schema(expected), result.Outputs["value"].Json));
    }

    /// <summary>範囲外や十進丸め、不正な文字を成功扱いしません。</summary>
    [Theory]
    [InlineData("byte", "256")]
    [InlineData("byte", "-1")]
    [InlineData("byte", "1.1")]
    [InlineData("integer", "9223372036854775808")]
    [InlineData("decimal", "0.12345678901234567890123456789")]
    [InlineData("decimal", "1e-29")]
    [InlineData("decimal", "1.0e-2147483648")]
    [InlineData("decimal", "1e2147483648")]
    [InlineData("number", "NaN")]
    [InlineData("number", "1e400")]
    [InlineData("number", "1e-400")]
    [InlineData("auto", "1e400")]
    [InlineData("boolean", "2")]
    [InlineData("char", "")]
    [InlineData("char", "ab")]
    [InlineData("auto", "{\"x\":1,\"x\":2}")]
    public void InvalidScalarsFail(string type, string text)
    {
        NodeResult result = Constant(type, text);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>単独サロゲートを置換文字にして保存しません。</summary>
    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    public void BrokenUnicodeIsRejected(string escaped)
    {
        NodeResult result = Evaluate(new ConstantValueNode(), "{\"type\":\"char\",\"text\":\"" + escaped + "\"}", []);
        Assert.NotEqual(EOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>型値は明示オブジェクトで渡し、通常の文字列を型と取り違えません。</summary>
    [Fact]
    public void TypeDescriptorsComposeAndValidateNestedValues()
    {
        NodeResult type = Evaluate(new TypeValueNode(), "{\"type\":\"array\"}", new() { ["elementType"] = Json("{\"type\":\"byte\"}") });
        Assert.Equal(EOutcome.Succeeded, type.Outcome);
        Assert.Equal("array", type.Outputs["value"].Json.GetProperty("type").GetString());
        NodeResult valid = Evaluate(new ArrayValueNode(), "{}", new() { ["item"] = Json("[0,255]"), ["elementType"] = type.Outputs["value"] });
        Assert.Equal(EOutcome.Succeeded, valid.Outcome);
        NodeResult invalid = Evaluate(new ArrayValueNode(), "{}", new() { ["item"] = Json("[256]"), ["elementType"] = type.Outputs["value"] });
        Assert.Equal(EOutcome.Failed, invalid.Outcome);
        Assert.Equal(EOutcome.Failed, Evaluate(new ConstantValueNode(), "{\"text\":\"12\"}", new() { ["type"] = Json("\"integer\"") }).Outcome);
        NodeResult data = Evaluate(new ArrayValueNode(), "{}", new() { ["item"] = type.Outputs["value"] });
        Assert.Equal("array", data.Outputs["value"].Json[0].GetProperty("type").GetString());
        Assert.Equal(0, Evaluate(new ArrayValueNode(), "{}", []).Outputs["value"].Json.GetArrayLength());
    }

    /// <summary>重複の拒否と明示置換を分け、元の入力を変更しません。</summary>
    [Fact]
    public void ObjectCompositionRequiresExplicitReplacement()
    {
        NodeValue original = Json("{\"x\":1}");
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal) { ["base"] = original, ["value"] = Json("[2,null]") };
        Assert.Equal(EOutcome.Failed, Evaluate(new KeyValueNode(), "{\"key\":\"x\"}", inputs).Outcome);
        NodeResult replaced = Evaluate(new KeyValueNode(), "{\"key\":\"x\",\"duplicate\":\"replace\"}", inputs);
        Assert.Equal(EOutcome.Succeeded, replaced.Outcome);
        Assert.Equal(2, replaced.Outputs["value"].Json.GetProperty("x")[0].GetInt32());
        Assert.Equal(1, original.Json.GetProperty("x").GetInt32());
    }

    /// <summary>オブジェクト型は必須・任意と値型を再帰的に区別します。</summary>
    [Theory]
    [InlineData("{\"age\":25}", true)]
    [InlineData("{\"age\":25,\"nickname\":null}", true)]
    [InlineData("{\"age\":\"25\"}", false)]
    [InlineData("{}", false)]
    [InlineData("{\"age\":25,\"extra\":0}", false)]
    public void ObjectTypeEnforcesItsProperties(string text, bool success)
    {
        NodeValue type = Json("{\"type\":\"object\",\"properties\":{\"age\":{\"type\":\"integer\"},\"nickname\":{\"type\":\"string\",\"optional\":true}}}");
        NodeResult result = Evaluate(new ConstantValueNode(), JsonSerializer.Serialize(new { text }), new() { ["type"] = type });
        Assert.Equal(success, result.Outcome == EOutcome.Succeeded);
    }

    /// <summary>通常実行も同じ評価を使い、HTTPメソッドは文字列を出します。</summary>
    /// <returns>非同期入口の検証です。</returns>
    [Fact]
    public async Task ExecutionUsesPureResultAsync()
    {
        foreach (INode registered in ValuesNodeRegistration.Create())
        {
            Assert.IsAssignableFrom<IValueNode>(registered);
        }
        HttpMethodNode node = new();
        string settings = "{\"method\":\"PATCH\"}";
        NodeResult evaluated = Evaluate(node, settings, []);
        NodeResult executed = await node.ExecuteAsync(NodeTestContext.Create(settings)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, executed.Outcome);
        Assert.Equal("PATCH", executed.Outputs["value"].Json.GetString());
        Assert.True(JsonElement.DeepEquals(evaluated.Outputs["value"].Json, executed.Outputs["value"].Json));
        Assert.Equal(EOutcome.NotExecuted, Evaluate(node, "{\"method\":\"CUSTOM\"}", []).Outcome);
    }

    /// <summary>定数の公開設定から評価します。</summary>
    /// <returns>実ノードの結果です。</returns>
    private static NodeResult Constant(string type, string text) => Evaluate(new ConstantValueNode(), JsonSerializer.Serialize(new { type, text }), []);

    /// <summary>所有するJSON入力を作成します。</summary>
    /// <returns>独立した入力です。</returns>
    private static NodeValue Json(string text) => NodeValue.FromJson(NodeSettings.Schema(text));

    /// <summary>通常コンパイルと同じ型・設定で純粋評価を呼びます。</summary>
    /// <returns>構築結果です。</returns>
    private static NodeResult Evaluate(PureValueNode node, string settings, Dictionary<string, NodeValue> inputs)
        => node.Evaluate(new NodeDefinition("test", node.Descriptor.Type, NodeSettings.Schema(settings)), inputs);
}
