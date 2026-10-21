using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>型推測に必要な、変換を伴わない定数と型定義の組み立てだけを読み取ります。</summary>
internal sealed class WorkflowLiteralValues(IReadOnlyDictionary<string, NodeDefinition> nodes, ILookup<(string Node, string Port), WireDefinition> incoming)
{
    private readonly Dictionary<string, NodeValue?> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> visiting = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding strictUtf8 = new(false, true);
    private static readonly string[] successRoutes = ["success", "*"];

    /// <summary>接続で上書きされた設定に、保存されている古い値を使いません。</summary>
    /// <returns>そのまま確認できる設定値、または未確定です。</returns>
    internal NodeValue? Setting(NodeDefinition node, string name)
    {
        string port = SettingPorts.Name(name);
        if (HasInput(node, port))
        {
            return Input(node, port);
        }
        if (node.Settings.ValueKind == JsonValueKind.Object && node.Settings.TryGetProperty(name, out JsonElement value))
        {
            return NodeValue.FromJson(value);
        }
        return null;
    }

    /// <summary>未接続と、値がまだ分からない接続を区別します。</summary>
    /// <returns>接続または固定入力があるかです。</returns>
    internal bool HasInput(NodeDefinition node, string port) => incoming[(node.Id, port)].Any() || node.Inputs?.ContainsKey(port) == true;

    /// <summary>成功値の接続だけをたどり、外部処理や任意の拡張ノードは評価しません。</summary>
    /// <returns>既知の固定値、または未確定です。</returns>
    internal NodeValue? Input(NodeDefinition node, string port)
    {
        WireDefinition[] wires = incoming[(node.Id, port)].Take(2).ToArray();
        if (wires.Length == 0)
        {
            NodeValue? literal = node.Inputs?.GetValueOrDefault(port);
            return Bounded(literal);
        }
        string valuePort = "value";
        (string Output, string Property)? field = wires.Length == 1 ? PropertyPorts.Parse(wires[0].FromPort)
                                                                    : null;
        if (wires.Length != 1 || !string.Equals(field?.Output ?? wires[0].FromPort, valuePort, StringComparison.Ordinal) || !successRoutes.Contains(wires[0].When, StringComparer.Ordinal))
        {
            return null;
        }
        NodeValue? value = Output(wires[0].FromNode);
        if (value is not null && field is { } property)
        {
            value = JsonPointer.Select(value, PropertyPorts.Name(string.Empty, property.Property));
        }
        if (value is not null && wires[0].Path is { } path)
        {
            if (4096 < path.Length || !JsonPointer.IsValid(path))
            {
                return null;
            }
            try
            {
                value = JsonPointer.Select(value, path);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return value;
    }

    /// <summary>循環・深さ・処理件数と結果の大きさを制限します。</summary>
    /// <returns>変換せず確認できる値、または未確定です。</returns>
    internal NodeValue? Output(string id)
    {
        if (values.TryGetValue(id, out NodeValue? known))
        {
            return known;
        }
        if (32 <= visiting.Count || 1024 <= values.Count || !visiting.Add(id))
        {
            return null;
        }
        NodeValue? value = null;
        try
        {
            if (nodes.TryGetValue(id, out NodeDefinition? node))
            {
                value = node.Type switch
                {
                    "value.constant" => Constant(node),
                    "value.code" => Setting(node, "code"),
                    "value.comment" => Input(node, "value"),
                    "value.type" => Type(node),
                    "value.keyValue" => Object(node),
                    "data.keyValue.get" => KeyValueLookupTypes.Literal(node, this),
                    "data.defaultIfNull" => DefaultIfNullValues.Literal(node, this),
                    "data.keyValue.key" => KeyValueParts.Read(Input(node, "value"), true),
                    "data.keyValue.value" => KeyValueParts.Read(Input(node, "value"), false),
                    "value.array" => Array(node),
                    "value.dictionary" => DictionaryValueTypes.Literal(node, this),
                    "data.concat" => ConcatValues.Literal(node, this),
                    "text.substring" => Substring(node),
                    "data.object.expand" => StructuredTypeSettings.ObjectPreview(Input(node, "value"), Setting(node, "objectMode"), HasInput(node, "$settings.objectMode")),
                    "http.method" => Setting(node, "method"),
                    "value.interface" => Setting(node, "interfaceId"),
                    "value.peer" => Setting(node, "peerId"),
                    "value.file" or "value.directory" => PathTypeMetadata.Apply(Setting(node, "path") ?? NodeValue.Null, new("path")),
                    _ => null,
                };
            }
            value = Bounded(value);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            value = null;
        }
        finally
        {
            visiting.Remove(id);
        }
        values[id] = value;
        return value;
    }

    /// <summary>接続された長さが未確定の場合は、省略指定と取り違えてプレビューしません。</summary>
    /// <returns>全引数が既知の場合の部分文字列です。</returns>
    private NodeValue? Substring(NodeDefinition node)
    {
        NodeValue? length = Input(node, "length");
        string lengthPort = "length";
        if (length is null && HasInput(node, lengthPort))
        {
            return null;
        }
        return SubstringValues.Extract(Input(node, "value"), Input(node, "startIndex"), length);
    }

    /// <summary>文字列・元のJSONと、標準表記が実行時の数値型と一致する定数だけを読みます。</summary>
    /// <returns>実行時の変換が不要な固定値です。</returns>
    private NodeValue? Constant(NodeDefinition node)
    {
        NodeValue? textValue = Setting(node, "text");
        if (textValue?.Json.ValueKind != JsonValueKind.String || textValue.Json.GetString() is not { } text || 131072 < text.Length)
        {
            return null;
        }
        _ = strictUtf8.GetByteCount(text);
        string typePort = "type";
        string? type;
        if (HasInput(node, typePort))
        {
            NodeValue? descriptor = Input(node, typePort);
            ValueTypeDefinition? definition = descriptor is null ? null
                                                                 : ValueTypeInference.Descriptor(descriptor);
            if (definition?.Schema is not null || definition?.ElementType is not null || definition?.Properties is not null)
            {
                return null;
            }
            type = definition?.Type;
        }
        else
        {
            NodeValue? supplied = Setting(node, typePort);
            type = supplied?.GetString();
            if (supplied is null && !HasInput(node, SettingPorts.Name(typePort)))
            {
                type = "auto";
            }
        }
        return LiteralScalarValues.Parse(text, type);
    }

    /// <summary>配列・オブジェクトの型を、接続された型定義も含めて組み立てます。</summary>
    /// <returns>共通の型定義検査を通った値です。</returns>
    private NodeValue? Type(NodeDefinition node)
    {
        NodeValue? name = Setting(node, "type");
        if (name?.Json.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        JsonObject type = new() { ["type"] = name.Json.GetString() };
        foreach (string port in new[] { "elementType", "properties" })
        {
            if (HasInput(node, port))
            {
                NodeValue? input = Input(node, port);
                if (input?.Kind != EValueKind.Json)
                {
                    return null;
                }
                type[port] = JsonNode.Parse(input.Json.GetRawText());
            }
        }
        string optional = "optional";
        NodeValue? option = Setting(node, optional);
        if (option is not null)
        {
            if (option.Json.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }
            type[optional] = option.Json.GetBoolean();
        }
        else
        {
            if (HasInput(node, SettingPorts.Name(optional)))
            {
                return null;
            }
        }
        NodeValue result = NodeValue.FromJson(JsonSerializer.SerializeToElement(type));
        return ValueTypeInference.Descriptor(result) is null ? null
                                                             : result;
    }

    /// <summary>既知のキーと値を持つオブジェクトを、元の値を変更せず組み立てます。</summary>
    /// <returns>重複方針まで確定した固定値です。</returns>
    private NodeValue? Object(NodeDefinition node)
    {
        string keyPort = "key";
        string basePort = "base";
        NodeValue? key = HasInput(node, keyPort) ? Input(node, keyPort)
                                                 : Setting(node, keyPort);
        NodeValue? value = Input(node, "value");
        if (key?.Json.ValueKind != JsonValueKind.String || key.Json.GetString() is not { } name || value?.Kind != EValueKind.Json)
        {
            return null;
        }
        _ = strictUtf8.GetByteCount(name);
        JsonObject result = new();
        if (HasInput(node, basePort))
        {
            NodeValue? basis = Input(node, basePort);
            if (basis?.Json.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            result = JsonNode.Parse(basis.Json.GetRawText())!.AsObject();
        }
        string replace = "replace";
        string duplicate = "duplicate";
        if (result.ContainsKey(name) && !string.Equals(Setting(node, duplicate)?.GetString(), replace, StringComparison.Ordinal))
        {
            return null;
        }
        result[name] = JsonNode.Parse(value.Json.GetRawText());
        return PathTypeMetadata.WithProperty(NodeValue.FromJson(JsonSerializer.SerializeToElement(result)), Input(node, basePort), name, value);
    }

    /// <summary>型変換を要求していない固定配列だけを組み立てます。</summary>
    /// <returns>元の配列と追加値を確認できる場合の値です。</returns>
    private NodeValue? Array(NodeDefinition node)
    {
        string items = "items";
        string item = "item";
        string element = "elementType";
        JsonArray result = new();
        if (HasInput(node, items))
        {
            NodeValue? source = Input(node, items);
            if (source?.Json.ValueKind != JsonValueKind.Array || 1024 < source.Json.GetArrayLength())
            {
                return null;
            }
            result = JsonNode.Parse(source.Json.GetRawText())!.AsArray();
        }
        if (HasInput(node, item))
        {
            NodeValue? value = Input(node, item);
            if (value?.Kind != EValueKind.Json)
            {
                return null;
            }
            result.Add(JsonNode.Parse(value.Json.GetRawText()));
        }
        string modeKey = "elementMode";
        string automatic = "auto";
        NodeValue? mode = Setting(node, modeKey);
        if (HasInput(node, SettingPorts.Name(modeKey)) || mode is not null && !string.Equals(mode.GetString(), automatic, StringComparison.Ordinal))
        {
            return null;
        }
        NodeValue array = NodeValue.FromJson(JsonSerializer.SerializeToElement(result));
        if (HasInput(node, element))
        {
            NodeValue? definition = Input(node, element);
            ValueTypeDefinition? type = null;
            if (definition is not null)
            {
                type = ValueTypeInference.Descriptor(definition);
            }
            if (type is null || type.Schema is not null || type.ElementType is not null || type.Properties is not null
                || array.Json.EnumerateArray().Any(value => !MatchesSimpleType(value, type)))
            {
                return null;
            }
        }
        List<ValueTypeDefinition?> types = [];
        if (Input(node, items) is { } sourceValue)
        {
            types.AddRange(Enumerable.Range(0, sourceValue.Json.GetArrayLength()).Select(index => ValueTypeInference.Select(sourceValue.DataType, "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        if (Input(node, item) is { } appended)
        {
            types.Add(appended.DataType);
        }
        ValueTypeDefinition? declared = Input(node, element) is { } elementValue ? ValueTypeInference.Descriptor(elementValue)
                                                                                 : null;
        if (result.Count == 0)
        {
            declared ??= Input(node, items)?.DataType?.ElementType;
        }
        return PathTypeMetadata.Apply(array, PathTypeMetadata.ArrayType(types, declared));
    }

    /// <summary>変換や値域判定が不要な型だけ静的な値を確認します。</summary>
    /// <returns>構築結果を確定できる場合だけtrueです。</returns>
    private static bool MatchesSimpleType(JsonElement value, ValueTypeDefinition type)
    {
        if (value.ValueKind == JsonValueKind.Null && type.Optional)
        {
            return true;
        }
        return type.Type switch
        {
            "any" => true,
            "string" or "path" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false,
        };
    }

    /// <summary>静的確認だけで巨大な値やバイト列を複製しません。</summary>
    /// <returns>読み取り上限内のJSON値です。</returns>
    private static NodeValue? Bounded(NodeValue? value) => value?.Kind == EValueKind.Json && value.Json.GetRawText().Length <= 131072 ? value
                                                                                                                                      : null;
}
