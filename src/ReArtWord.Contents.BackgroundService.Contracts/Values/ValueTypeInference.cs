using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>静的な型推測で共有する深さと項目数を制限したJSON構造の検査です。</summary>
public static class ValueTypeInference
{
    /// <summary>選択候補が同じ型の場合だけ、選択状態に依存せず出力型を確定します。</summary>
    /// <returns>両候補に共通する型、または未確定のanyです。</returns>
    internal static ValueTypeDefinition CommonChoice(ValueTypeDefinition? first, ValueTypeDefinition? second)
    {
        if (first is not null && second is not null && Same(first, second))
        {
            return first;
        }
        return new("any");
    }

    private static readonly string[] names = ["any", "boolean", "byte", "integer", "int64", "number", "decimal", "char", "string", "path", "array", "object"];

    /// <summary>表示可能な明示型の名前か確認します。</summary>
    /// <returns>既知の型名の場合はtrueです。</returns>
    public static bool KnownName(string name) => names.Contains(name, StringComparer.Ordinal);

    /// <summary>既知の構造的不適合だけを拒否し、値域や任意値は実行時検証に残します。</summary>
    /// <returns>明示型に適合する可能性があるかです。</returns>
    public static bool CanAssign(ValueTypeDefinition? expected, ValueTypeDefinition? actual)
        => CanAssign(expected, actual, 0);

    /// <summary>深い型や外部から渡された循環型で再帰を無制限にしません。</summary>
    /// <returns>有限範囲で確認できた互換性です。</returns>
    private static bool CanAssign(ValueTypeDefinition? expected, ValueTypeDefinition? actual, int depth)
    {
        string any = "any";
        if (64 <= depth || expected is null || actual is null || actual.Optional || string.Equals(expected.Type, any, StringComparison.Ordinal) || string.Equals(actual.Type, any, StringComparison.Ordinal))
        {
            return true;
        }
        if (!string.Equals(JsonFamily(expected.Type), JsonFamily(actual.Type), StringComparison.Ordinal))
        {
            return false;
        }
        if (!CanAssign(expected.ElementType, actual.ElementType, depth + 1))
        {
            return false;
        }
        if (!CanAssign(expected.KeyType, actual.KeyType, depth + 1) || !CanAssign(expected.ValueType, actual.ValueType, depth + 1)
            || expected.ValueType is not null && actual.Properties?.Values.Any(value => !CanAssign(expected.ValueType, value, depth + 1)) == true)
        {
            return false;
        }
        return expected.Properties is null || actual.Properties is null || expected.Properties.All(property => CanAssign(property.Value, actual.Properties.GetValueOrDefault(property.Key), depth + 1));
    }

    /// <summary>精度や文字数を実行前に断定せず、同じJSON区分を照合します。</summary>
    /// <returns>包含関係を持つ値区分です。</returns>
    internal static string JsonFamily(string type) => type switch
    {
        "byte" or "integer" or "int64" or "decimal" => "number",
        "char" or "path" => "string",
        "type definition" => "object",
        _ => type,
    };

    /// <summary>論理演算が成功した場合の真偽値またはInt64整数を補足します。</summary>
    /// <returns>対応する入力型を確認できない場合はnullです。</returns>
    public static ValueTypeDefinition? LogicResult(ValueTypeDefinition? input) => input?.Type switch
    {
        "boolean" => new("boolean"),
        "byte" or "integer" or "int64" or "number" or "decimal" => new("integer"),
        _ => null,
    };

    /// <summary>左右で同じ種類を確認できる場合だけ結果型を補足します。</summary>
    /// <returns>一致した成功型、またはnullです。</returns>
    public static ValueTypeDefinition? LogicResult(ValueTypeDefinition? left, ValueTypeDefinition? right)
    {
        ValueTypeDefinition? result = LogicResult(left);
        if (Same(result, LogicResult(right)))
        {
            return result;
        }
        return null;
    }

    /// <summary>任意データを型定義と取り違えず読み取ります。</summary>
    /// <returns>検証できた型です。</returns>
    public static ValueTypeDefinition? Descriptor(NodeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind != EValueKind.Json || 131072 < value.Json.GetRawText().Length)
        {
            return null;
        }
        int remaining = 1024;
        try
        {
            return ValueTypeDescriptors.ReadDescriptor(value.Json, 0, ref remaining);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>重複キーを例外や上書きにしないプロパティ型取得です。</summary>
    /// <returns>一意なプロパティ型、またはnullです。</returns>
    public static IReadOnlyDictionary<string, ValueTypeDefinition>? Properties(NodeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.Object || 131072 < value.Json.GetRawText().Length)
        {
            return null;
        }
        int remaining = 1024;
        return ValueTypeDescriptors.ReadProperties(value.Json, 0, ref remaining);
    }

    /// <summary>辞書参照一致ではなく上限内の構造を比較します。</summary>
    /// <returns>同じ型かどうかです。</returns>
    public static bool Same(ValueTypeDefinition? first, ValueTypeDefinition? second)
    {
        int remaining = 1024;
        return Compare(first, second, 0, ref remaining);
    }

    /// <summary>深い構造を再シリアル化せず有限に比較します。</summary>
    /// <returns>構造が一致した場合はtrueです。</returns>
    private static bool Compare(ValueTypeDefinition? first, ValueTypeDefinition? second, int depth, ref int remaining)
    {
        if (first is null || second is null || 64 <= depth || --remaining < 0 || first.Optional != second.Optional
            || !string.Equals(first.Type, second.Type, StringComparison.Ordinal))
        {
            return false;
        }
        if ((first.ElementType is not null || second.ElementType is not null) && !Compare(first.ElementType, second.ElementType, depth + 1, ref remaining))
        {
            return false;
        }
        if ((first.KeyType is not null || second.KeyType is not null) && !Compare(first.KeyType, second.KeyType, depth + 1, ref remaining)
            || (first.ValueType is not null || second.ValueType is not null) && !Compare(first.ValueType, second.ValueType, depth + 1, ref remaining))
        {
            return false;
        }
        if (first.Properties is null && second.Properties is null)
        {
            return true;
        }
        if (first.Properties is null || second.Properties is null || first.Properties.Count != second.Properties.Count)
        {
            return false;
        }
        foreach ((string key, ValueTypeDefinition property) in first.Properties)
        {
            if (!second.Properties.TryGetValue(key, out ValueTypeDefinition? other) || !Compare(property, other, depth + 1, ref remaining))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>項目や配列要素をたどり、途中のnullや範囲外の可能性を保持します。</summary>
    /// <returns>欠損の可能性を含む抽出型です。</returns>
    public static ValueTypeDefinition? Select(ValueTypeDefinition? type, string? path)
    {
        if (path is null)
        {
            return type;
        }
        if (!JsonPointer.IsValid(path) || 4096 < path.Length)
        {
            return null;
        }
        if (path.Length == 0)
        {
            return type;
        }
        string obj = "object";
        string array = "array";
        bool optional = false;
        foreach (string part in path.Split('/').Skip(1))
        {
            if (type is null)
            {
                return null;
            }
            optional |= type.Optional;
            string key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (string.Equals(type.Type, obj, StringComparison.Ordinal))
            {
                ValueTypeDefinition? selected = type.Properties?.GetValueOrDefault(key);
                type = selected ?? type.ValueType;
                optional |= selected is null;
            }
            else
            {
                if (!string.Equals(type.Type, array, StringComparison.Ordinal) || key.Length == 0 || key.Any(character => character < '0' || '9' < character)
                    || 1 < key.Length && key[0] == '0')
                {
                    return null;
                }
                type = type.Properties?.GetValueOrDefault(key) ?? type.ElementType;
                optional = true;
            }
        }
        if (type is not null && optional)
        {
            return type with { Optional = true };
        }
        return type;
    }

    /// <summary>全訪問数にも上限を設けて巨大な定数の検査を制限します。</summary>
    /// <returns>確認できた値型です。</returns>
    public static ValueTypeDefinition? FromJson(JsonElement value, int depth)
    {
        int remaining = 16384;
        return ReadJson(value, depth, ref remaining);
    }

    /// <summary>JSONを一度ずつ走査します。</summary>
    /// <returns>既知の構造です。</returns>
    private static ValueTypeDefinition? ReadJson(JsonElement value, int depth, ref int remaining)
    {
        if (64 <= depth || --remaining < 0)
        {
            return null;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.True: case JsonValueKind.False: return new("boolean");
            case JsonValueKind.String: return new("string");
            case JsonValueKind.Number:
                if (value.TryGetInt64(out _))
                {
                    return new("integer");
                }
                return new("number");
            case JsonValueKind.Object:
                Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    ValueTypeDefinition propertyType = ReadJson(property.Value, depth + 1, ref remaining) ?? new("any");
                    if (128 <= properties.Count || !properties.TryAdd(property.Name, propertyType))
                    {
                        return new("object");
                    }
                }
                return new("object", Properties: properties);
            case JsonValueKind.Array:
                if (value.GetArrayLength() == 0 || 4096 < value.GetArrayLength())
                {
                    return new("array");
                }
                ValueTypeDefinition? element = ReadJson(value[0], depth + 1, ref remaining);
                foreach (JsonElement item in value.EnumerateArray().Skip(1))
                {
                    element = InferredArrayTypes.Merge(element, ReadJson(item, depth + 1, ref remaining));
                    if (element is null)
                    {
                        return new("array");
                    }
                }
                return new("array", element);
            default: return null;
        }
    }

    /// <summary>詳細が未知のJSONと、Binaryもあり得るANYを区別します。</summary>
    /// <returns>宣言から保証できる値区分です。</returns>
    internal static ValueTypeDefinition? DeclaredOutput(NodeDescriptor? descriptor, string port)
        => descriptor?.Outputs.FirstOrDefault(output => string.Equals(output.Name, port, StringComparison.Ordinal))?.ValueKind switch
        {
            EPortValueKind.Binary => new("binary"),
            EPortValueKind.Json => new("any"),
            EPortValueKind.PcSelection => new(Targets.PcSelectionPorts.TypeName),
            _ => null,
        };
}
