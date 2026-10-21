using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Osc;

/// <summary>OSCパケットと、直接配線できるアドレス・引数を相互変換します。</summary>
internal static class OscNodeValues
{
    private static string MatchBoolean => "boolean";
    private static string MatchTrue => "T";
    private static string MatchFalse => "F";
    private static string MatchNull => "N";
    private static string MatchBlob => "b";

    /// <summary>通信設定を持たない解析でも同じ型指定欄を使用します。</summary>
    internal static JsonElement ArgumentSchema
    {
        get;
    } = NodeSettings.Schema("""{"type":"object","additionalProperties":false,"properties":{"argumentTypes":{"x-runtime-input":false,"type":["array","object"],"maxItems":64,"items":{"type":["string","object"]},"description":"Choose ordered OSC types here, or connect a type definition or an array of types. Integer uses Int32 (i), explicit Int64 uses h, number uses Float64 (d); use OSC tags for explicit wire precision. Each argument gets its own port. Unset uses packets; an empty list means no arguments."}}}""");

    /// <summary>既存の通信設定へOSCの受信条件と引数型を加えます。</summary>
    /// <returns>編集・検証で共有する設定です。</returns>
    internal static JsonElement Schema(JsonElement schema, bool sending)
    {
        JsonObject root = JsonNode.Parse(schema.GetRawText())!.AsObject();
        JsonObject properties = root["properties"]!.AsObject();
        string description = "Leave empty to receive every OSC address, or enter an exact address such as /control/start.";
        if (sending)
        {
            description = "OSC address to send. A connected Address input takes precedence.";
        }
        properties["address"] = new JsonObject { ["type"] = "string", ["description"] = description };
        string framing = "framing";
        if (properties.ContainsKey(framing))
        {
            properties["framing"]!["enum"] = new JsonArray("lengthPrefix");
        }
        properties["argumentTypes"] = JsonNode.Parse(ArgumentSchema.GetProperty("properties").GetProperty("argumentTypes").GetRawText());
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>固定ポートを実行中に増減させる配線と、不正なアドレスを拒否します。</summary>
    /// <returns>訂正が必要な設定です。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [];
        NodeSettings settings = new(definition.Settings);
        string types = "argumentTypes";
        if (settings.Get(types).ValueKind != JsonValueKind.Undefined && OscPorts.Types(definition.Settings) is null)
        {
            issues.Add(new("osc.arguments.invalid", "OSC引数型は対応する型タグまたは型定義を順序付きで指定してください。any、decimal、配列、object、任意値の型はOSC引数にできません。", definition.Id, types));
        }
        string? address = settings.ReadText("address");
        if (!string.IsNullOrEmpty(address) && (!address.StartsWith('/') || address.Contains('\0', StringComparison.Ordinal)))
        {
            issues.Add(new("osc.address.invalid", "OSCアドレスは / から始め、終端文字を含めないでください。", definition.Id, "address"));
        }
        return issues;
    }

    /// <summary>既存パケット入力、または固定引数ポートから送信パケットを作ります。</summary>
    /// <returns>型タグと引数順を保持したOSCパケットです。</returns>
    internal static byte[] Encode(NodeExecutionContext context)
    {
        IReadOnlyList<string>? types = OscPorts.Types(context.Settings);
        NodeSettings settings = new(context.Settings);
        string argumentTypes = "argumentTypes";
        string elements = "elements";
        string? address = context.Inputs.GetValueOrDefault("address")?.GetString() ?? settings.ReadText("address");
        if (types is null)
        {
            if (settings.Get(argumentTypes).ValueKind != JsonValueKind.Undefined)
            {
                throw new FormatException("OSC引数の型指定が不正です。");
            }
            JsonElement packet = context.Inputs["value"].Json;
            if (string.IsNullOrEmpty(address))
            {
                return OscPacketCodec.Encode(packet);
            }
            JsonObject message = JsonNode.Parse(packet.GetRawText())!.AsObject();
            if (message.ContainsKey(elements))
            {
                throw new FormatException("OSC bundle全体のアドレスは上書きできません。");
            }
            message["address"] = address;
            return OscPacketCodec.Encode(JsonSerializer.SerializeToElement(message));
        }
        JsonArray arguments = [];
        for (int index = 0; index < types.Count; index++)
        {
            NodeValue value = context.Inputs[OscPorts.Argument(index)];
            string tag = types[index];
            if (string.Equals(tag, MatchBlob, StringComparison.Ordinal) != (value.Kind == EValueKind.Binary))
            {
                throw new FormatException("OSC引数の値と指定した型が一致しません。");
            }
            if (string.Equals(tag, MatchBoolean, StringComparison.Ordinal))
            {
                tag = value.Json.GetBoolean() ? "T"
                                              : "F";
            }
            if ((string.Equals(tag, MatchTrue, StringComparison.Ordinal) && value.Json.ValueKind != JsonValueKind.True) || (string.Equals(tag, MatchFalse, StringComparison.Ordinal) && value.Json.ValueKind != JsonValueKind.False)
                || (string.Equals(tag, MatchNull, StringComparison.Ordinal) && value.Json.ValueKind != JsonValueKind.Null))
            {
                throw new FormatException("OSC引数の値と指定した型が一致しません。");
            }
            JsonNode? data = string.Equals(tag, MatchBlob, StringComparison.Ordinal) ? JsonValue.Create(Convert.ToBase64String(value.Binary.Span))
                                                                                     : JsonNode.Parse(value.Json.GetRawText());
            arguments.Add(new JsonObject { ["type"] = tag, ["value"] = data });
        }
        return OscPacketCodec.Encode(JsonSerializer.SerializeToElement(new JsonObject { ["address"] = address, ["arguments"] = arguments }));
    }

    /// <summary>パケットを保持したまま、単一メッセージのアドレスと引数を取り出します。</summary>
    /// <returns>対象外のアドレスはnull。型違いは受信失敗です。</returns>
    internal static NodeResult? Decode(ReadOnlySpan<byte> bytes, JsonElement settings)
    {
        JsonElement packet = OscPacketCodec.Decode(bytes);
        string addressKey = "address";
        string? filter = new NodeSettings(settings).ReadText(addressKey);
        IReadOnlyList<string>? types = OscPorts.Types(settings);
        if (!packet.TryGetProperty(addressKey, out JsonElement address))
        {
            // bundleは時刻と構造を失わないよう、従来のパケット出力だけで受け渡します。
            return string.IsNullOrEmpty(filter) && types is null ? NodeResult.Success(NodeValue.FromJson(packet))
                                                                 : NodeResult.Failure("osc.bundle.requiresPacket", "OSC bundleはパケット形式で受け取ってください。アドレスと引数は単一メッセージで使用できます。");
        }
        if (!string.IsNullOrEmpty(filter) && !string.Equals(filter, address.GetString(), StringComparison.Ordinal))
        {
            return null;
        }
        JsonElement arguments = packet.GetProperty("arguments");
        if (types is not null && types.Count != arguments.GetArrayLength())
        {
            return NodeResult.Failure("osc.arguments.mismatch", "OSC引数の数が設定と一致しません。");
        }
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromJson(packet),
            ["address"] = NodeValue.FromJson(address),
            ["arguments"] = NodeValue.FromJson(arguments),
        };
        if (types is not null)
        {
            for (int index = 0; index < types.Count; index++)
            {
                JsonElement argument = arguments[index];
                string tag = argument.GetProperty("type").GetString()!;
                if (!string.Equals(tag, types[index], StringComparison.Ordinal) && !(string.Equals(types[index], MatchBoolean, StringComparison.Ordinal) && (string.Equals(tag, MatchTrue, StringComparison.Ordinal) || string.Equals(tag, MatchFalse, StringComparison.Ordinal))))
                {
                    return NodeResult.Failure("osc.arguments.mismatch", "OSC引数の型が設定と一致しません。自動変換は行いません。");
                }
                outputs[OscPorts.Argument(index)] = tag switch
                {
                    "b" => NodeValue.FromBytes(argument.GetProperty("value").GetBytesFromBase64()),
                    "T" => NodeValue.FromObject(true),
                    "F" => NodeValue.FromObject(false),
                    "N" => NodeValue.FromObject<object?>(null),
                    "h" => NodeValue.FromJson(argument.GetProperty("value")) with { DataType = new("int64") },
                    _ => NodeValue.FromJson(argument.GetProperty("value")),
                };
            }
        }
        return NodeResult.Success() with { Outputs = outputs };
    }
}
