using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;

/// <summary>通信せず、OSC送信ノードがそのまま受け取れる型付きパケットを組み立てます。</summary>
public sealed class OscPacketNode : PureValueNode
{
    /// <summary>元パケットのアドレスを上書きでき、未接続口を引数に数えない可変入力です。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(OscPorts.PacketNodeType, "Build OSC packet", "Communication", """{"type":"object","properties":{}}""",
        inputs: [new("address", EPortValueKind.Json), new("packet", EPortValueKind.Json), .. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ConcatPorts.Name(index)))],
        outputs: [new("value", EPortValueKind.Json)]) with
    {
        Description = "Build an OSC message without sending it. Supply arguments in port order, one array, or connect an existing message to Original OSC packet. A supplied address overrides the original packet address; otherwise keep it. Integers use i when they fit Int32, otherwise h; explicit Int64 uses h even for small values. Numbers use d, strings s, binary b, booleans T/F and null N. Tagged arguments preserve explicit wire types. Up to 64 arguments; arrays and packets cannot be mixed with other argument inputs.",
    };

    /// <summary>引数順を固定し、未対応値や不正なパケットは出力しません。</summary>
    /// <returns>送信可能なaddress/arguments形式のObjectです。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        string addressPort = "address";
        inputs.TryGetValue(addressPort, out NodeValue? address);
        inputs.TryGetValue("packet", out NodeValue? packetInput);
        NodeValue[] supplied = inputs.Where(pair => 0 < ConcatPorts.Index(pair.Key)).OrderBy(pair => ConcatPorts.Index(pair.Key)).Select(pair => pair.Value).ToArray();
        if (packetInput is not null && supplied.Length != 0)
        {
            throw new FormatException("osc.packet.mixedArguments");
        }
        string argumentsKey = "arguments";
        if (supplied.Length == 1 && supplied[0].Kind == EValueKind.Json && supplied[0].Json.ValueKind == JsonValueKind.Object
            && supplied[0].Json.TryGetProperty(argumentsKey, out _))
        {
            // 保存済みWFの汎用引数口へのパケット接続も維持します。
            packetInput = supplied[0];
        }
        if (packetInput is not null)
        {
            JsonElement original = packetInput.Json;
            if (packetInput.Kind != EValueKind.Json || original.ValueKind != JsonValueKind.Object
                || !original.TryGetProperty(addressPort, out JsonElement originalAddress) || original.EnumerateObject().Count() != 2
                || !original.TryGetProperty(argumentsKey, out JsonElement originalArguments) || originalArguments.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("osc.packet.invalid");
            }
            address ??= NodeValue.FromJson(originalAddress);
            supplied = [NodeValue.FromJson(originalArguments)];
        }
        if (address is null || address.Kind != EValueKind.Json || address.Json.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("osc.address.required");
        }
        if (supplied.Length == 1 && supplied[0].Kind == EValueKind.Json && supplied[0].Json.ValueKind == JsonValueKind.Array)
        {
            if (ConcatPorts.Maximum < supplied[0].Json.GetArrayLength())
            {
                throw new FormatException("osc.arguments.limit");
            }
            NodeValue array = supplied[0];
            supplied = Enumerable.Range(0, array.Json.GetArrayLength()).Select(index => JsonPointer.Select(array, "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        }
        JsonArray arguments = [];
        long size = address.Json.GetRawText().Length;
        if (1048576 < size)
        {
            throw new FormatException("osc.packet.tooLarge");
        }
        foreach (NodeValue value in supplied)
        {
            size += value.Kind == EValueKind.Binary ? value.Binary.Length * 2L
                                                    : value.Json.GetRawText().Length;
            if (1048576 < size)
            {
                throw new FormatException("osc.packet.tooLarge");
            }
            arguments.Add(Argument(value));
        }
        JsonElement packet = JsonSerializer.SerializeToElement(new JsonObject { ["address"] = address.Json.GetString(), ["arguments"] = arguments });

        // 実際の符号化と同じ境界で、文字列終端・型別範囲・Base64を検証します。
        if (1048576 < OscPacketCodec.Encode(packet).Length)
        {
            throw new FormatException("osc.packet.tooLarge");
        }
        return NodeValue.FromJson(packet);
    }

    /// <summary>文字列を数値と誤認せず、既存の型付き引数も保持します。</summary>
    /// <returns>一個のOSC引数です。</returns>
    private static JsonObject Argument(NodeValue value)
    {
        if (value.Kind == EValueKind.Binary)
        {
            return new JsonObject { ["type"] = "b", ["value"] = Convert.ToBase64String(value.Binary.Span) };
        }
        JsonElement json = value.Json;
        if (json.ValueKind == JsonValueKind.Object)
        {
            return Tagged(json);
        }
        string tag = json.ValueKind switch
        {
            JsonValueKind.String => "s",
            JsonValueKind.True => "T",
            JsonValueKind.False => "F",
            JsonValueKind.Null => "N",
            JsonValueKind.Number when value.DataType?.Type is "int64" && json.TryGetInt64(out _) => "h",
            JsonValueKind.Number when json.TryGetInt32(out _) => "i",
            JsonValueKind.Number when json.TryGetInt64(out _) => "h",
            JsonValueKind.Number when 0 <= json.GetRawText().IndexOfAny(['.', 'e', 'E']) => "d",
            _ => throw new FormatException("osc.argument.notConvertible"),
        };
        return new JsonObject { ["type"] = tag, ["value"] = JsonNode.Parse(json.GetRawText()) };
    }

    /// <summary>受信ノードの型付き引数と、明示したwire精度を受け取ります。</summary>
    /// <returns>値と型タグが一致する引数です。</returns>
    private static JsonObject Tagged(JsonElement json)
    {
        string typeKey = "type", valueKey = "value";
        if (!json.TryGetProperty(typeKey, out JsonElement type) || type.ValueKind != JsonValueKind.String
            || json.EnumerateObject().Any(property => !string.Equals(property.Name, typeKey, StringComparison.Ordinal) && !string.Equals(property.Name, valueKey, StringComparison.Ordinal)))
        {
            throw new FormatException("osc.argument.notConvertible");
        }
        string tag = type.GetString()!;
        bool hasValue = json.TryGetProperty(valueKey, out JsonElement value);
        bool invalid = tag is not ("i" or "h" or "f" or "d" or "s" or "b" or "t" or "T" or "F" or "N" or "I")
            || !hasValue && tag is not ("T" or "F" or "N" or "I")
            || hasValue && (tag, value.ValueKind) is ("T", not JsonValueKind.True) or ("F", not JsonValueKind.False) or ("N" or "I", not JsonValueKind.Null);
        int expectedProperties = hasValue ? 2
                                          : 1;
        if (invalid || json.EnumerateObject().Count() != expectedProperties)
        {
            throw new FormatException("osc.argument.typeMismatch");
        }
        JsonNode? data = tag switch { "T" => JsonValue.Create(true), "F" => JsonValue.Create(false), _ => null };
        if (hasValue)
        {
            data = JsonNode.Parse(value.GetRawText());
        }
        return new JsonObject { ["type"] = tag, ["value"] = data };
    }
}
