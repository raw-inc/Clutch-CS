using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>整数の既定精度と明示Int64を、実際に符号化したOSCの境界で確認します。</summary>
public sealed class OscIntegerTypesTests
{
    /// <summary>通常整数はInt32へ収め、収まらない値だけInt64で保持します。</summary>
    [Theory]
    [InlineData(0L, "i")]
    [InlineData(1L, "i")]
    [InlineData(int.MinValue, "i")]
    [InlineData(int.MaxValue, "i")]
    [InlineData(-2147483649L, "h")]
    [InlineData(2147483648L, "h")]
    [InlineData(long.MinValue, "h")]
    [InlineData(long.MaxValue, "h")]
    public void UntypedIntegersUseTheSmallestSupportedWireType(long value, string tag)
    {
        NodeValue packet = Packet(NodeValue.FromObject(value));
        byte[] bytes = OscPacketCodec.Encode(packet.Json);
        JsonElement argument = Assert.Single(OscPacketCodec.Decode(bytes).GetProperty("arguments").EnumerateArray());
        Assert.Equal(tag, argument.GetProperty("type").GetString());
        Assert.Equal(value, argument.GetProperty("value").GetInt64());
        int expectedLength = tag switch { "i" => 12, _ => 16 };
        Assert.Equal(expectedLength, bytes.Length);
    }

    /// <summary>JSONでは同じ値でも、利用者が指定した型だけが64bitを強制します。</summary>
    [Theory]
    [InlineData("auto", "i")]
    [InlineData("integer", "i")]
    [InlineData("int64", "h")]
    public void ConstantTypeControlsSmallIntegerPrecision(string type, string tag)
    {
        NodeResult result = new ConstantValueNode().Evaluate(new("constant", "value.constant", JsonSerializer.SerializeToElement(new { type, text = "1" })), new Dictionary<string, NodeValue>());
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(tag, Tag(Packet(ContractJson.Copy(result.Outputs["value"])).Json));
        string declared = type switch { "auto" => "integer", _ => type };
        Assert.Equal([tag], OscPorts.ArgumentTypes(JsonSerializer.SerializeToElement(new { type = declared })));
    }

    /// <summary>変換による明示と解除を同じ値のまま確認します。</summary>
    /// <returns>通常ノード実行の確認です。</returns>
    [Fact]
    public async Task ConversionCanSetAndClearExplicitInt64Async()
    {
        NodeResult explicitValue = await new ConvertNode().ExecuteAsync(NodeTestContext.Create("""{"targetType":"int64"}""", NodeValue.FromObject("1"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, explicitValue.Outcome);
        Assert.Equal("h", Tag(Packet(explicitValue.Outputs["value"]).Json));
        NodeResult ordinary = await new ConvertNode().ExecuteAsync(NodeTestContext.Create("""{"targetType":"integer"}""", explicitValue.Outputs["value"])).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, ordinary.Outcome);
        Assert.Equal("i", Tag(Packet(ordinary.Outputs["value"]).Json));
    }

    /// <summary>解析の個別出力や再シリアル化で、受信したhをiへ狭めません。</summary>
    [Fact]
    public void ReceivedInt64PreservesPrecisionAcrossValueCopyAndRebuild()
    {
        NodeValue original = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("""{"address":"/n","arguments":[{"type":"h","value":1}]}"""));
        OscParseNode parse = new();
        NodeResult parsed = parse.Evaluate(new("parse", OscPorts.ParseNodeType, JsonSerializer.Deserialize<JsonElement>("""{"argumentTypes":["h"]}""")),
            new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromBytes(OscPacketCodec.Encode(original.Json)) });
        Assert.Equal(EOutcome.Succeeded, parsed.Outcome);
        NodeValue value = ContractJson.Copy(parsed.Outputs["argument1"].Copy());
        Assert.Equal("int64", PathTypeMetadata.TypeOf(value)!.Type);
        Assert.Equal("h", Tag(Packet(value).Json));
    }

    /// <summary>通常整数とInt64を同じ配列へ入れても、片方の精度で他方を上書きしません。</summary>
    [Theory]
    [InlineData(false, "ih")]
    [InlineData(true, "hi")]
    public void MixedArraysPreserveEachExplicitIntegerType(bool reverse, string tags)
    {
        NodeValue ordinary = NodeValue.FromObject(1), wide = PathTypeMetadata.Apply(NodeValue.FromObject(2), new("int64"));
        NodeValue first = reverse ? wide
                                  : ordinary;
        NodeValue second = reverse ? ordinary
                                   : wide;
        ArrayValueNode node = new();
        NodeDefinition definition = new("array", "value.array", JsonSerializer.SerializeToElement(new { }));
        NodeResult initial = node.Evaluate(definition, new Dictionary<string, NodeValue> { ["item"] = first });
        NodeResult appended = node.Evaluate(definition, new Dictionary<string, NodeValue> { ["items"] = initial.Outputs["value"], ["item"] = second });
        Assert.Equal(EOutcome.Succeeded, appended.Outcome);
        NodeValue copied = ContractJson.Copy(appended.Outputs["value"].Copy());
        JsonElement arguments = OscPacketCodec.Decode(OscPacketCodec.Encode(Packet(copied).Json)).GetProperty("arguments");
        Assert.Equal(tags, string.Concat(arguments.EnumerateArray().Select(argument => argument.GetProperty("type").GetString())));
        Assert.Equal(tags[..1], Tag(Packet(JsonPointer.Select(copied, "/0")).Json));
        Assert.Equal(tags[1..], Tag(Packet(JsonPointer.Select(copied, "/1")).Json));
        NodeValue joined = ConcatValues.Join([copied, copied]);
        Assert.Equal("array", ConcatValues.ResultType([PathTypeMetadata.TypeOf(copied), new("array", new("integer"))])!.Type);
        JsonElement concatenated = OscPacketCodec.Decode(OscPacketCodec.Encode(Packet(joined).Json)).GetProperty("arguments");
        Assert.Equal(tags + tags, string.Concat(concatenated.EnumerateArray().Select(argument => argument.GetProperty("type").GetString())));
    }

    /// <summary>Binaryの明示int64フィールドも、構造から取り出した後まで64bitを保持します。</summary>
    /// <returns>実ノードで解析した結果です。</returns>
    [Fact]
    public async Task BinaryInt64FieldRetainsItsDeclaredPrecisionAsync()
    {
        NodeResult decoded = await new ExtractNode().ExecuteAsync(NodeTestContext.Create("""{"mode":"binary","fields":[{"name":"number","type":"int64","offset":0}]}""",
            NodeValue.FromBytes(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, decoded.Outcome);
        Assert.Equal("h", Tag(Packet(JsonPointer.Select(decoded.Outputs["value"], "/number")).Json));
    }

    /// <summary>型タグは明示指定を優先します。</summary>
    [Theory]
    [InlineData("i")]
    [InlineData("h")]
    public void ExplicitWireTagsRemainUnchanged(string tag)
    {
        NodeValue value = NodeValue.FromObject(new { type = tag, value = 1 });
        Assert.Equal(tag, Tag(Packet(value).Json));
    }

    /// <summary>同じ実ノードを全入力経路で使用します。</summary>
    /// <returns>成功したOSCパケットです。</returns>
    private static NodeValue Packet(NodeValue value)
    {
        NodeResult result = new OscPacketNode().Evaluate(new("packet", OscPorts.PacketNodeType, JsonSerializer.SerializeToElement(new { })),
            new Dictionary<string, NodeValue> { ["address"] = NodeValue.FromObject("/n"), ["input1"] = value });
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        return result.Outputs["value"];
    }

    /// <summary>実際のwire形式から型タグを復元します。</summary>
    /// <returns>一引数の型です。</returns>
    private static string Tag(JsonElement packet) => Assert.Single(OscPacketCodec.Decode(OscPacketCodec.Encode(packet)).GetProperty("arguments").EnumerateArray()).GetProperty("type").GetString()!;
}
