using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>OSC解析の型・精度・必須入力・不正パケットの境界を検証します。</summary>
public sealed class OscParseNodeTests
{
    /// <summary>Objectとバイト列から、受信と同じタグ・精度・Binaryを取り出します。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsesEveryReceiverTypeWithoutChangingTheInput(bool binary)
    {
        JsonElement packet = JsonSerializer.Deserialize<JsonElement>("""{"address":"/cue","arguments":[{"type":"i","value":2147483647},{"type":"h","value":9007199254740993},{"type":"f","value":1.25},{"type":"d","value":1.0000000000000002},{"type":"s","value":"hello"},{"type":"b","value":"AP8="},{"type":"T","value":true},{"type":"F","value":false},{"type":"N","value":null},{"type":"t","value":18446744073709551615}]}""");
        string original = ContractJson.Hash(packet);
        NodeValue input = NodeValue.FromJson(packet);
        if (binary)
        {
            input = NodeValue.FromBytes(OscPacketCodec.Encode(packet));
        }
        NodeResult result = Parse(input, """{"argumentTypes":["i","h","f","d","s","b","boolean","F","N","t"]}""");
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("/cue", result.Outputs["address"].GetString());
        Assert.Equal(2147483647, result.Outputs["argument1"].Json.GetInt32());
        Assert.Equal(9007199254740993L, result.Outputs["argument2"].Json.GetInt64());
        Assert.Equal(1.25f, result.Outputs["argument3"].Json.GetSingle());
        Assert.Equal(1.0000000000000002, result.Outputs["argument4"].Json.GetDouble());
        Assert.Equal("hello", result.Outputs["argument5"].GetString());
        Assert.Equal(new byte[] { 0, 255 }, result.Outputs["argument6"].Binary.ToArray());
        Assert.True(result.Outputs["argument7"].Json.GetBoolean());
        Assert.False(result.Outputs["argument8"].Json.GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.Outputs["argument9"].Json.ValueKind);
        Assert.Equal(ulong.MaxValue, result.Outputs["argument10"].Json.GetUInt64());
        Assert.Equal(10, result.Outputs["arguments"].Json.GetArrayLength());
        Assert.Equal(original, ContractJson.Hash(result.Outputs["value"].Json));
        Assert.Equal(original, ContractJson.Hash(packet));
    }

    /// <summary>型タグの精度と順序・個数が違う場合、部分的な成功値を返しません。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[\"h\"]")]
    [InlineData("[\"s\"]")]
    [InlineData("[\"i\",\"i\"]")]
    public void TypeAndCountMismatchReturnNoOutputs(string types)
    {
        NodeResult result = Parse(Value("""{"address":"/cue","arguments":[{"type":"i","value":5}]}"""), "{\"argumentTypes\":" + types + "}");
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("osc.arguments.mismatch", result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>型なしでは一般出力を保持し、空型一覧は引数ゼロを検証します。</summary>
    [Fact]
    public void PacketModeAndExplicitEmptyArgumentsAreDistinct()
    {
        NodeValue packet = Value("""{"address":"/empty","arguments":[]}""");
        foreach (string settings in new[] { "{}", "{\"argumentTypes\":[]}" })
        {
            NodeResult result = Parse(packet, settings);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(3, result.Outputs.Count);
            Assert.Empty(result.Outputs["arguments"].Json.EnumerateArray());
        }
        NodeResult populated = Parse(Value("""{"address":"/value","arguments":[{"type":"s","value":"text"}]}"""));
        Assert.Equal(3, populated.Outputs.Count);
        Assert.Equal("text", populated.Outputs["arguments"].Json[0].GetProperty("value").GetString());
    }

    /// <summary>Bundleの時刻と入れ子を保持し、型付き単一メッセージとしては拒否します。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BundlePreservesItsStructureWithoutEmittingIndividualArguments(bool binary)
    {
        NodeValue packet = Value("""{"timetag":1,"elements":[{"timetag":2,"elements":[{"address":"/later","arguments":[]}]}]}""");
        NodeValue input = packet;
        if (binary)
        {
            input = NodeValue.FromBytes(OscPacketCodec.Encode(packet.Json));
        }
        NodeResult result = Parse(input);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Single(result.Outputs);
        Assert.Equal(ContractJson.Hash(packet.Json), ContractJson.Hash(result.Outputs["value"].Json));
        NodeResult typed = Parse(input, "{\"argumentTypes\":[]}");
        Assert.Equal("osc.bundle.requiresPacket", typed.ReasonCode);
        Assert.Empty(typed.Outputs);
    }

    /// <summary>値の種類・形状・範囲が不正なJSONを解析成功にしません。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("12")]
    [InlineData("\"/path\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"address\":\"bad\",\"arguments\":[]}")]
    [InlineData("{\"address\":\"/bad\",\"arguments\":[{\"type\":\"i\",\"value\":2147483648}]}")]
    [InlineData("{\"address\":\"/bad\",\"arguments\":[{\"type\":\"b\",\"value\":\"bad!\"}]}")]
    [InlineData("{\"address\":\"/bad\",\"arguments\":[{\"type\":\"unknown\",\"value\":1}]}")]
    public void InvalidPacketObjectsFailWithoutPartialOutput(string json)
    {
        NodeResult result = Parse(Value(json));
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>切れたパケット、非ゼロpadding、未対応タグと過大入力を拒否します。</summary>
    [Fact]
    public void InvalidBinaryAndOversizedInputsFail()
    {
        byte[][] invalid = [[], [1], [47, 120, 0, 7], Encoding.ASCII.GetBytes("/x\0\0,x\0\0"), new byte[1048577]];
        foreach (byte[] bytes in invalid)
        {
            NodeResult result = Parse(NodeValue.FromBytes(bytes));
            Assert.Equal(EOutcome.Failed, result.Outcome);
            Assert.Empty(result.Outputs);
        }
        NodeValue large = NodeValue.FromObject(new { address = "/large", arguments = new[] { new { type = "s", value = new string('a', 1048576) } } });
        Assert.Equal(EOutcome.Failed, Parse(large).Outcome);
    }

    /// <summary>必須入力と構造設定の誤りを実行前に区別します。</summary>
    [Fact]
    public void MissingInputAndInvalidSettingsAreNotExecuted()
    {
        OscParseNode node = new();
        Assert.Equal(EOutcome.NotExecuted, node.Evaluate(new("parse", OscPorts.ParseNodeType, JsonSerializer.Deserialize<JsonElement>("{}")), new Dictionary<string, NodeValue>()).Outcome);
        Assert.Equal(EOutcome.NotExecuted, Parse(Value("{}"), "{\"argumentTypes\":[\"any\"]}").Outcome);
        Assert.Equal(EOutcome.NotExecuted, Parse(Value("{}"), "{\"address\":\"/filter\"}").Outcome);
    }

    /// <summary>型付きObject入力を作ります。</summary>
    /// <returns>所有権を持つJSON値です。</returns>
    private static NodeValue Value(string json) => NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json));

    /// <summary>副作用のない通常評価を実行します。</summary>
    /// <returns>解析ノードの実結果です。</returns>
    private static NodeResult Parse(NodeValue input, string settings = "{}") => new OscParseNode().Evaluate(new("parse", OscPorts.ParseNodeType, JsonSerializer.Deserialize<JsonElement>(settings)),
        new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = input });
}
