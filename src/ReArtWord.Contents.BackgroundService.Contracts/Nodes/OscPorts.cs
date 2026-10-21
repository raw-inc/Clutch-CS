using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>OSCの引数設定を、編集画面と実行時で同じポートへ対応させます。</summary>
public static class OscPorts
{
    /// <summary>通信を行わず単一OSCメッセージを組み立てるノードです。</summary>
    public static string PacketNodeType => "osc.packet";

    /// <summary>受け取ったOSCパケットを型付きの引数へ解析するノードです。</summary>
    public static string ParseNodeType => "osc.parse";

    private static string MatchBlob => "b";
    private static string MatchAddress => "address";
    private static string MatchArguments => "arguments";

    /// <summary>OSC引数の型指定を共有するノードかどうかを判定します。</summary>
    /// <returns>組み込みOSCノードならtrue。</returns>
    public static bool Supports(string type) => type is "osc.udp.receive" or "osc.udp.send" or "osc.tcp.listen" or "osc.tcp.request" or "osc.parse";

    /// <summary>引数の順序を保持するポート名を返します。</summary>
    /// <returns>一から始まる引数ポート名です。</returns>
    public static string Argument(int index) => "argument" + (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>固定した引数型を読み取ります。不正な編集中の設定ではポートを増やしません。</summary>
    /// <returns>OSC型タグの一覧。未指定はnullです。</returns>
    public static IReadOnlyList<string>? Types(JsonElement settings)
    {
        string key = "argumentTypes";
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(key, out JsonElement values)
            || values.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            return null;
        }
        return ArgumentTypes(values);
    }

    /// <summary>型タグ、型定義一つ、または順序付きの型定義配列を同じOSC契約へ変換します。</summary>
    /// <returns>対応しない型や任意値はnullです。精度を黙って落としません。</returns>
    public static IReadOnlyList<string>? ArgumentTypes(JsonElement values)
    {
        if (values.ValueKind == JsonValueKind.Object)
        {
            string? single = TypeTag(values);
            if (single is null)
            {
                return null;
            }
            return [single];
        }
        if (values.ValueKind != JsonValueKind.Array || 64 < values.GetArrayLength())
        {
            return null;
        }
        List<string> result = [];
        foreach (JsonElement value in values.EnumerateArray())
        {
            string? tag = TypeTag(value);
            if (tag is null)
            {
                return null;
            }
            result.Add(tag);
        }
        return result;
    }

    /// <summary>汎用型には欠損のないOSC表現を選び、wire精度を固定する場合はタグを使います。</summary>
    /// <returns>対応するタグまたは未対応です。</returns>
    private static string? TypeTag(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string? tag = value.GetString();
            if (ValueType(tag) is null)
            {
                return null;
            }
            return tag;
        }
        ValueTypeDefinition? type = ValueTypeInference.Descriptor(NodeValue.FromJson(value));
        if (type is null || type.Optional || type.Schema is not null)
        {
            return null;
        }
        return type.Type switch
        {
            "byte" or "integer" => "i", "int64" => "h", "number" => "d", "string" or "char" => "s", "boolean" => "boolean", _ => null,
        };
    }

    /// <summary>OSCの数値精度と、利用者に表示する値の種類を対応させます。</summary>
    /// <returns>未対応の型タグではnullです。</returns>
    public static ValueTypeDefinition? ValueType(string? tag) => tag switch
    {
        "i" or "t" => new("integer"),
        "h" => new("int64"),
        "f" or "d" => new("number"),
        "s" => new("string"),
        "b" => new("binary"),
        "T" or "F" or "boolean" => new("boolean"),
        "N" => new("null"),
        _ => null,
    };

    /// <summary>固定引数の数に合わせて入力または出力を追加します。</summary>
    /// <returns>OSC以外は元の定義をそのまま返します。</returns>
    public static NodeDescriptor? Resolve(NodeDescriptor? descriptor, NodeDefinition? node)
    {
        if (descriptor is null || node is null || !Supports(node.Type) || Types(node.Settings) is not { } types)
        {
            return descriptor;
        }
        bool sending = node.Type is "osc.udp.send" or "osc.tcp.request";
        List<PortDescriptor> ports = [];
        for (int index = 0; index < types.Count; index++)
        {
            EPortValueKind kind = string.Equals(types[index], MatchBlob, StringComparison.Ordinal) ? EPortValueKind.Binary
                                                                                                   : EPortValueKind.Json;
            ports.Add(new(Argument(index), kind, sending));
        }
        if (sending)
        {
            bool addressConfigured = node.Settings.TryGetProperty(MatchAddress, out JsonElement address) && address.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(address.GetString());
            PortDescriptor[] inputs = descriptor.Inputs.Where(port => port.Name is not "value").ToArray();
            int addressIndex = Array.FindIndex(inputs, port => string.Equals(port.Name, MatchAddress, StringComparison.Ordinal));
            if (0 <= addressIndex)
            {
                inputs[addressIndex] = inputs[addressIndex] with { Required = !addressConfigured };
            }
            return descriptor with { Inputs = [.. inputs, .. ports] };
        }
        return descriptor with { Outputs = [.. descriptor.Outputs, .. ports] };
    }

    /// <summary>設定で固定された引数の型を取得します。</summary>
    /// <returns>Address、引数、引数配列の型です。</returns>
    public static ValueTypeDefinition? PortType(NodeDefinition node, string port)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!Supports(node.Type))
        {
            return null;
        }
        if (string.Equals(port, MatchAddress, StringComparison.Ordinal))
        {
            return new("string");
        }
        if (string.Equals(port, MatchArguments, StringComparison.Ordinal))
        {
            return new("array");
        }
        if (Types(node.Settings) is { } types)
        {
            for (int index = 0; index < types.Count; index++)
            {
                if (string.Equals(port, Argument(index), StringComparison.Ordinal))
                {
                    return ValueType(types[index]);
                }
            }
        }
        return null;
    }
}
