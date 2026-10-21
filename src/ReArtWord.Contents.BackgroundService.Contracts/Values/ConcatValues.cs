using System.Buffers;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>結合の順序と同型検証を、実行と静的プレビューで共有します。</summary>
public static class ConcatValues
{
    private static string AnyType => "any";
    private static readonly string[] acceptedTypes = ["string", "char", "path", "array"];
    private static readonly UTF8Encoding utf8 = new(false, true);

    /// <summary>全入力が確定した場合だけ実行時と同じ結合をプレビューします。</summary>
    /// <returns>区切りを含めた固定値、または未確定です。</returns>
    internal static NodeValue? Literal(NodeDefinition node, WorkflowLiteralValues literals)
    {
        NodeValue?[] items = Enumerable.Range(1, ConcatPorts.Maximum).Select(ConcatPorts.Name).Where(port => literals.HasInput(node, port)).Select(port => literals.Input(node, port)).ToArray();
        string separatorPort = "separator";
        NodeValue? separator = literals.Input(node, separatorPort);
        if (separator is null && literals.HasInput(node, separatorPort) || items.Length == 0 || items.Any(item => item is null))
        {
            return null;
        }
        return Join(items.Select(item => item!).ToArray(), separator);
    }

    /// <summary>区切りを使ったPathの連結は通常の文字列として推測します。</summary>
    /// <returns>実行成功時に保証できる結合型です。</returns>
    internal static ValueTypeDefinition? Infer(NodeDefinition node, WorkflowLiteralValues literals, Func<string, string, bool, ValueTypeDefinition?> portType)
    {
        try
        {
            ValueTypeDefinition? result = ResultType(Enumerable.Range(1, ConcatPorts.Maximum).Select(ConcatPorts.Name)
                .Where(port => literals.HasInput(node, port)).Select(port => portType(node.Id, port, false)));
            string separatorPort = "separator", pathType = "path";
            if (string.Equals(result?.Type, pathType, StringComparison.Ordinal) && literals.HasInput(node, separatorPort))
            {
                NodeValue? separator = literals.Input(node, separatorPort);
                if (separator is null || separator.Json.ValueKind != JsonValueKind.Null && separator.GetString()?.Length != 0)
                {
                    return new("string");
                }
            }
            return result;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>未知の入力は実行時に検証し、既知の型の不一致だけを接続前に拒否します。</summary>
    /// <returns>結合が成功した場合に分かる出力型です。</returns>
    public static ValueTypeDefinition? ResultType(IEnumerable<ValueTypeDefinition?> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        ValueTypeDefinition? result = null;
        bool unknown = false;
        foreach (ValueTypeDefinition? type in types)
        {
            if (type is null || string.Equals(type.Type, AnyType, StringComparison.Ordinal))
            {
                unknown = true;
                continue;
            }
            if (!acceptedTypes.Contains(type.Type, StringComparer.Ordinal))
            {
                throw new FormatException("concat.type");
            }
            string name = type.Type switch { "char" => "string", _ => type.Type };
            result = Merge(result, type with { Type = name, Optional = false });
        }
        return (unknown, result?.Type) switch
        {
            (true, "path") => new("string"),
            (true, "array") when PathTypeMetadata.Copy(result) is not null => new("array"),
            _ => result,
        };
    }

    /// <summary>文字列、または同型の配列を順番に結合し、文字列の間だけ指定の区切りを挿入します。</summary>
    /// <returns>入力と所有権を共有しない結合値です。</returns>
    public static NodeValue Join(IReadOnlyList<NodeValue> values, NodeValue? separator = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || ConcatPorts.Maximum < values.Count)
        {
            throw new FormatException("concat.inputs");
        }
        string between = string.Empty;
        if (separator is not null)
        {
            if (separator.Kind != EValueKind.Json || separator.Json.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new FormatException("concat.separator");
            }
            between = separator.GetString() ?? string.Empty;
        }
        _ = utf8.GetByteCount(between);
        JsonValueKind kind = values[0].Json.ValueKind;
        if (kind == JsonValueKind.Array && between.Length != 0)
        {
            throw new FormatException("concat.separator.array");
        }
        long size = Math.Max(0, values.Count - 1) * (NodeValue.FromObject(between).PayloadSize() - 2);
        foreach (NodeValue value in values)
        {
            if (value.Kind != EValueKind.Json || value.Json.ValueKind != kind || kind is not (JsonValueKind.String or JsonValueKind.Array))
            {
                throw new FormatException("concat.type");
            }
            size += value.PayloadSize();
            if (1048576 < size)
            {
                throw new FormatException("concat.size");
            }
        }
        if (kind == JsonValueKind.String)
        {
            bool paths = between.Length == 0 && values.All(value => value.DataType?.Type is "path");
            if (paths)
            {
                return PathValues.Join(values);
            }
            StringBuilder text = new();
            bool first = true;
            foreach (NodeValue value in values)
            {
                string item = value.Json.GetString()!;
                _ = utf8.GetByteCount(item);
                if (!first)
                {
                    text.Append(between);
                }
                text.Append(item);
                first = false;
            }
            return NodeValue.FromObject(text.ToString());
        }
        if (4096 < values.Sum(value => value.Json.GetArrayLength()))
        {
            throw new FormatException("concat.items");
        }
        int remaining = 16384;
        ValueTypeDefinition? element = null;
        List<ValueTypeDefinition?> metadata = [];
        foreach (NodeValue value in values)
        {
            if (value.Json.GetArrayLength() == 0)
            {
                element = Merge(element, PathTypeMetadata.TypeOf(value)?.ElementType);
            }
            int index = 0;
            foreach (JsonElement item in value.Json.EnumerateArray())
            {
                ValueTypeDefinition? type = value.DataType?.Properties?.GetValueOrDefault(index.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? value.DataType?.ElementType;
                metadata.Add(type);
                element = Merge(element, ReadType(item, 0, ref remaining));
                index++;
            }
        }
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();
            foreach (NodeValue value in values)
            {
                foreach (JsonElement item in value.Json.EnumerateArray())
                {
                    item.WriteTo(writer);
                }
            }
            writer.WriteEndArray();
        }
        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        ValueTypeDefinition typeInfo = metadata.Count == 0 ? new("array", element)
                                                           : PathTypeMetadata.ArrayType(metadata);
        return PathTypeMetadata.Apply(NodeValue.FromJson(document.RootElement), typeInfo);
    }

    /// <summary>空配列は型を制約せず、既知の要素型とオブジェクト構造は一致を要求します。</summary>
    /// <returns>一致する結合型です。不一致は失敗として扱います。</returns>
    internal static ValueTypeDefinition? Merge(ValueTypeDefinition? left, ValueTypeDefinition? right, int depth = 0)
    {
        if (32 < depth)
        {
            throw new FormatException("concat.depth");
        }
        if (left is null || right is null)
        {
            return left ?? right;
        }
        if (string.Equals(left.Type, AnyType, StringComparison.Ordinal))
        {
            return right;
        }
        if (string.Equals(right.Type, AnyType, StringComparison.Ordinal))
        {
            return left;
        }
        if (!string.Equals(ValueTypeInference.JsonFamily(left.Type), ValueTypeInference.JsonFamily(right.Type), StringComparison.Ordinal) || left.Optional != right.Optional)
        {
            throw new FormatException("concat.elementType");
        }
        if (!string.Equals(left.Type, right.Type, StringComparison.Ordinal))
        {
            return new(ValueTypeInference.JsonFamily(left.Type));
        }
        ValueTypeDefinition? element = Merge(left.ElementType, right.ElementType, depth + 1);
        bool arrays = left.Type is "array";
        if (arrays)
        {
            return left with { ElementType = element, Properties = null };
        }
        if (left.Properties is null && right.Properties is null)
        {
            return left with { ElementType = element };
        }
        if (left.Properties is null || right.Properties is null)
        {
            return left with { Properties = left.Properties ?? right.Properties };
        }
        if (left.Properties.Count != right.Properties.Count)
        {
            throw new FormatException("concat.elementType");
        }
        Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
        foreach ((string key, ValueTypeDefinition type) in left.Properties)
        {
            if (!right.Properties.TryGetValue(key, out ValueTypeDefinition? other))
            {
                throw new FormatException("concat.elementType");
            }
            properties.Add(key, Merge(type, other, depth + 1)!);
        }
        return left with { Properties = properties };
    }

    /// <summary>混在を未知型として許可せず、既存のスカラー型判定を使って全要素を検査します。</summary>
    /// <returns>空配列を含めた比較用の型です。</returns>
    private static ValueTypeDefinition ReadType(JsonElement value, int depth, ref int remaining)
    {
        if (32 < depth || --remaining < 0)
        {
            throw new FormatException("concat.depth");
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            ValueTypeDefinition? element = null;
            foreach (JsonElement item in value.EnumerateArray())
            {
                element = Merge(element, ReadType(item, depth + 1, ref remaining));
            }
            return new("array", element);
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (128 <= properties.Count || !properties.TryAdd(property.Name, ReadType(property.Value, depth + 1, ref remaining)))
                {
                    throw new FormatException("concat.elementType");
                }
            }
            return new("object", Properties: properties);
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            _ = utf8.GetByteCount(value.GetString()!);
        }
        return ValueTypeInference.FromJson(value, depth) ?? new("null");
    }
}
