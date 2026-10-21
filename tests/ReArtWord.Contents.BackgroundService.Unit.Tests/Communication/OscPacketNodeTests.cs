using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>空引数、番号順、配列展開とOSCへの変換境界を確認します。</summary>
public sealed class OscPacketNodeTests
{
    /// <summary>元メッセージを保持し、明示されたアドレスだけを優先します。</summary>
    [Theory]
    [InlineData("input1")]
    [InlineData("packet")]
    public void PacketInputUsesItsAddressUnlessExplicitlyOverridden(string port)
    {
        NodeValue packet = Value("""{"address":"/original","arguments":[{"type":"i","value":42},{"type":"b","value":"AP8="}]}""");
        Dictionary<string, NodeValue> inputs = new() { [port] = packet };
        NodeDefinition definition = new("packet", OscPorts.PacketNodeType, JsonSerializer.SerializeToElement(new { }));
        OscPacketNode node = new();
        NodeResult original = node.Evaluate(definition, inputs);
        Assert.Equal(EOutcome.Succeeded, original.Outcome);
        Assert.Equal("/original", original.Outputs["value"].Json.GetProperty("address").GetString());
        inputs["address"] = NodeValue.FromObject("/override") with { DataType = new("path") };
        NodeResult changed = node.Evaluate(definition, inputs);
        Assert.Equal(EOutcome.Succeeded, changed.Outcome);
        JsonElement decoded = OscPacketCodec.Decode(OscPacketCodec.Encode(changed.Outputs["value"].Json));
        Assert.Equal("/override", decoded.GetProperty("address").GetString());
        Assert.Equal("i", decoded.GetProperty("arguments")[0].GetProperty("type").GetString());
        Assert.Equal(42, decoded.GetProperty("arguments")[0].GetProperty("value").GetInt32());
        Assert.Equal(new byte[] { 0, 255 }, decoded.GetProperty("arguments")[1].GetProperty("value").GetBytesFromBase64());
        Assert.Equal("/original", packet.Json.GetProperty("address").GetString());
        inputs["address"] = NodeValue.FromObject("");
        Assert.Equal(EOutcome.Failed, node.Evaluate(definition, inputs).Outcome);
        Assert.Equal(EOutcome.Failed, node.Evaluate(definition, new Dictionary<string, NodeValue>()).Outcome);
        Assert.Equal(EOutcome.Failed, Build((port, packet), ("input2", NodeValue.FromObject(1))).Outcome);
        Assert.Equal(EOutcome.Failed, Build(("packet", packet), ("input1", packet)).Outcome);
    }

    /// <summary>死活通知も標準の空型タグを持つメッセージになります。</summary>
    [Fact]
    public void NoArgumentsAndEmptyArrayProduceTheSamePacket()
    {
        NodeResult empty = Build();
        NodeResult array = Build(("input1", Value("[]")));
        Assert.Equal(EOutcome.Succeeded, empty.Outcome);
        Assert.Equal(EOutcome.Succeeded, array.Outcome);
        byte[] bytes = OscPacketCodec.Encode(empty.Outputs["value"].Json);
        Assert.Equal(bytes, OscPacketCodec.Encode(array.Outputs["value"].Json));
        JsonElement decoded = OscPacketCodec.Decode(bytes);
        Assert.Equal("/all/ping", decoded.GetProperty("address").GetString());
        Assert.Empty(decoded.GetProperty("arguments").EnumerateArray());
        Assert.Contains((byte)',', bytes);
    }

    /// <summary>接続追加順や空き口に影響されず、精度・Binary・nullを保持します。</summary>
    [Fact]
    public void ArgumentsUseNumericPortOrderAndPreserveKinds()
    {
        NodeResult result = Build(("input10", NodeValue.FromBytes(new byte[] { 0, 128, 255 })), ("input2", NodeValue.FromObject(9007199254740993L)),
            ("input1", NodeValue.FromObject("001")), ("input4", NodeValue.FromObject(1.25)), ("input5", NodeValue.FromObject(true)),
            ("input6", NodeValue.FromObject(false)), ("input7", NodeValue.Null));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        JsonElement arguments = OscPacketCodec.Decode(OscPacketCodec.Encode(result.Outputs["value"].Json)).GetProperty("arguments");
        Assert.Equal(["s", "h", "d", "T", "F", "N", "b"], arguments.EnumerateArray().Select(item => item.GetProperty("type").GetString()));
        Assert.Equal("001", arguments[0].GetProperty("value").GetString());
        Assert.Equal(9007199254740993L, arguments[1].GetProperty("value").GetInt64());
        Assert.Equal(new byte[] { 0, 128, 255 }, arguments[6].GetProperty("value").GetBytesFromBase64());
    }

    /// <summary>通常配列と受信済みの型タグ付き引数配列の両方を受け取れます。</summary>
    [Theory]
    [InlineData("[1,1.25,\"text\",true,null]", "idsTN")]
    [InlineData("[{\"type\":\"i\",\"value\":1},{\"type\":\"f\",\"value\":1.25},{\"type\":\"T\"},{\"type\":\"N\"}]", "ifTN")]
    [InlineData("[{\"type\":\"t\",\"value\":18446744073709551615},{\"type\":\"b\",\"value\":\"AP8=\"}]", "tb")]
    public void SingleArrayExpandsWithoutDiscardingExplicitTags(string json, string tags)
    {
        NodeResult result = Build(("input2", Value(json)));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        JsonElement decoded = OscPacketCodec.Decode(OscPacketCodec.Encode(result.Outputs["value"].Json));
        Assert.Equal(tags, string.Concat(decoded.GetProperty("arguments").EnumerateArray().Select(item => item.GetProperty("type").GetString())));
    }

    /// <summary>未対応構造・範囲外・矛盾したタグを黙って文字列化しません。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[[1]]")]
    [InlineData("9223372036854775808")]
    [InlineData("1e999")]
    [InlineData("\"bad\\u0000text\"")]
    [InlineData("{\"type\":\"T\",\"value\":false}")]
    [InlineData("{\"type\":\"i\",\"value\":2147483648}")]
    [InlineData("{\"type\":\"b\",\"value\":\"invalid\"}")]
    [InlineData("{\"type\":\"unknown\",\"value\":1}")]
    [InlineData("{\"type\":\"s\",\"value\":\"text\",\"extra\":1}")]
    public void InvalidArgumentDoesNotProduceAPartialPacket(string json)
    {
        NodeResult result = Build(("input1", Value(json)));
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>配列と独立引数の混在、上限超過、OSCでないアドレスを拒否します。</summary>
    [Fact]
    public void AmbiguousInputsAndInvalidAddressesFail()
    {
        Assert.Equal(EOutcome.Succeeded, Build(("packet", Value("{\"address\":\"/old\",\"arguments\":[]}"))).Outcome);
        Assert.Equal(EOutcome.Failed, Build(("packet", Value("[]"))).Outcome);
        Assert.Equal(EOutcome.Failed, Build(("packet", NodeValue.Null)).Outcome);
        Assert.Equal(EOutcome.Failed, Build(("packet", NodeValue.FromBytes(new byte[] { 1 }))).Outcome);
        Assert.Equal(EOutcome.Failed, Build(("input1", Value("[1]")), ("input2", NodeValue.FromObject(2))).Outcome);
        Assert.Equal(EOutcome.Failed, Build(("input1", NodeValue.FromObject(Enumerable.Range(0, 65)))).Outcome);
        Assert.Equal(EOutcome.Succeeded, Build(("input1", NodeValue.FromObject(Enumerable.Range(0, 64)))).Outcome);
        Assert.Equal(EOutcome.Failed, Build(("input1", NodeValue.FromObject(new string('x', 1048577)))).Outcome);
        foreach (string address in new[] { "", "relative", "C:/file", "/bad\0address" })
        {
            Assert.Equal(EOutcome.Failed, Build(("address", NodeValue.FromObject(address))).Outcome);
        }
    }

    /// <summary>通常の純粋ノード評価を使用します。</summary>
    /// <returns>通信を伴わない組み立て結果です。</returns>
    private static NodeResult Build(params (string Port, NodeValue Value)[] values)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal) { ["address"] = NodeValue.FromObject("/all/ping") };
        foreach ((string port, NodeValue value) in values)
        {
            inputs[port] = value;
        }
        return new OscPacketNode().Evaluate(new("packet", OscPorts.PacketNodeType, JsonSerializer.SerializeToElement(new { })), inputs);
    }

    /// <summary>元の数値表現を保持します。</summary>
    /// <returns>JSON値です。</returns>
    private static NodeValue Value(string json) => NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json));
}
