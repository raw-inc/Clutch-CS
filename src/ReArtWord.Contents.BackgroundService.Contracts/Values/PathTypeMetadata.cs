using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>JSONの表現を変えず、入れ子のPath・Int64・辞書の型情報を値と一緒に保持します。</summary>
public static class PathTypeMetadata
{
    /// <summary>Pathを正規化し、Int64を検証します。通常型へ戻す場合は明示型の補足を外します。</summary>
    /// <returns>JSON本文と有限な型情報を持つ値です。</returns>
    public static NodeValue Apply(NodeValue value, ValueTypeDefinition? type, bool? windows = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (type is null)
        {
            return value;
        }
        ValueTypeDefinition? metadata = Copy(type);
        if (metadata is null)
        {
            return value.DataType is null ? value
                                          : value with { DataType = null };
        }
        if (value.Kind != EValueKind.Json)
        {
            throw new FormatException("path.json");
        }
        JsonNode? root = JsonNode.Parse(value.Json.GetRawText());
        int remaining = 16384;
        root = Normalize(root, metadata, 0, ref remaining, windows);
        return NodeValue.FromJson(JsonSerializer.SerializeToElement(root)) with { DataType = metadata };
    }

    /// <summary>保存した明示型を、実データから分かる他の型へ重ねます。</summary>
    /// <returns>通常の型推測を消さない値の型です。</returns>
    public static ValueTypeDefinition? TypeOf(NodeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind != EValueKind.Json)
        {
            return new("binary");
        }
        return Overlay(ValueTypeInference.FromJson(value.Json, 0), value.DataType, 0);
    }

    /// <summary>可変辞書を共有せず、Path・Int64・辞書の意味付けを持つ型の枝を複製します。</summary>
    /// <returns>JSON本文だけでは復元できない型情報、含まなければnullです。</returns>
    public static ValueTypeDefinition? Copy(ValueTypeDefinition? type)
    {
        int remaining = 16384;
        return Copy(type, 0, ref remaining);
    }

    /// <summary>全要素で一致する場合だけ、配列全体で共通する明示型を求めます。</summary>
    /// <returns>共通型、または個別要素の型保持が必要なnullです。</returns>
    public static ValueTypeDefinition? Common(IEnumerable<ValueTypeDefinition?> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        using IEnumerator<ValueTypeDefinition?> iterator = types.GetEnumerator();
        if (!iterator.MoveNext())
        {
            return null;
        }
        ValueTypeDefinition? first = Copy(iterator.Current);
        while (iterator.MoveNext())
        {
            if (!ValueTypeInference.Same(first, Copy(iterator.Current)))
            {
                return null;
            }
        }
        return first;
    }

    /// <summary>配列で共通しない明示型は添字ごとに保持し、通常整数までInt64へ広げません。</summary>
    /// <returns>共通要素型または個別要素の型情報です。</returns>
    public static ValueTypeDefinition ArrayType(IReadOnlyList<ValueTypeDefinition?> types, ValueTypeDefinition? declared = null)
    {
        ArgumentNullException.ThrowIfNull(types);
        ValueTypeDefinition? element = declared ?? Common(types);
        if (element is not null)
        {
            return new("array", element);
        }
        Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
        for (int index = 0; index < types.Count; index++)
        {
            if (Copy(types[index]) is { } type)
            {
                properties.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture), type);
            }
        }
        return new("array", Properties: properties.Count == 0 ? null
                                                              : properties);
    }

    /// <summary>オブジェクトの一項目を追加・置換したとき、同じ項目のPath情報も更新します。</summary>
    /// <returns>値の項目と対応するPath情報を持つオブジェクトです。</returns>
    public static NodeValue WithProperty(NodeValue value, NodeValue? basis, string key, NodeValue item)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(item);
        if (basis?.DataType is null && item.DataType is null)
        {
            return value;
        }
        Dictionary<string, ValueTypeDefinition> properties = new(basis?.DataType?.Properties ?? new Dictionary<string, ValueTypeDefinition>(), StringComparer.Ordinal);
        properties.Remove(key);
        if (Copy(item.DataType) is { } type)
        {
            properties[key] = type;
        }
        return Apply(value, new("object", Properties: properties));
    }

    /// <summary>型の走査量と深さを制限します。</summary>
    /// <returns>JSONだけでは表せない明示型を保持する独立した枝です。</returns>
    private static ValueTypeDefinition? Copy(ValueTypeDefinition? type, int depth, ref int remaining)
    {
        if (type is null)
        {
            return null;
        }
        if (32 < depth || --remaining < 0)
        {
            throw new FormatException("path.typeDepth");
        }
        string array = "array";
        bool scalar = type.Type is "path" or "int64";
        if (scalar)
        {
            return new(type.Type, Optional: type.Optional);
        }
        ValueTypeDefinition? element = Copy(type.ElementType, depth + 1, ref remaining);
        bool isArray = string.Equals(type.Type, array, StringComparison.Ordinal);
        ValueTypeDefinition? dictionary = isArray && element is not null ? new(array, element, Optional: type.Optional)
                                                                         : null;
        if (type.KeyType is not null && type.ValueType is not null)
        {
            dictionary = new("object", Optional: type.Optional, KeyType: ContractJson.Copy(type.KeyType), ValueType: ContractJson.Copy(type.ValueType));
        }
        if (type.Properties is null)
        {
            return dictionary;
        }
        Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal);
        foreach ((string key, ValueTypeDefinition child) in type.Properties)
        {
            if (Copy(child, depth + 1, ref remaining) is { } copied)
            {
                properties.Add(key, copied);
            }
        }
        string container = isArray ? array
                                   : "object";
        return properties.Count == 0 ? dictionary
                                     : (dictionary ?? new(container, Optional: type.Optional)) with { Properties = properties.ToFrozenDictionary(StringComparer.Ordinal) };
    }

    /// <summary>存在しない任意項目やnullを新しい値で埋めません。</summary>
    /// <returns>Pathの文字列だけを書き換えたJSONです。</returns>
    private static JsonNode? Normalize(JsonNode? node, ValueTypeDefinition type, int depth, ref int remaining, bool? windows)
    {
        if (32 < depth || --remaining < 0)
        {
            throw new FormatException("path.valueDepth");
        }
        if (node is null && type.Optional)
        {
            return null;
        }
        string path = "path";
        string int64 = "int64";
        if (string.Equals(type.Type, int64, StringComparison.Ordinal)
            && (node is not JsonValue integer || !integer.TryGetValue(out long _)))
        {
            throw new FormatException("value.int64");
        }
        if (string.Equals(type.Type, path, StringComparison.Ordinal))
        {
            if (node is not JsonValue value || !value.TryGetValue(out string? text) || text is null)
            {
                throw new FormatException("path.string");
            }
            return JsonValue.Create(windows is { } target ? PathValues.ToNative(text, target)
                                                          : PathValues.Normalize(text));
        }
        if (type.ElementType is not null && node is not JsonArray || type.Properties is not null && node is not (JsonObject or JsonArray))
        {
            throw new FormatException("path.structure");
        }
        if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                ValueTypeDefinition? element = type.Properties?.GetValueOrDefault(index.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? type.ElementType;
                if (element is null)
                {
                    continue;
                }
                JsonNode? next = Normalize(array[index], element, depth + 1, ref remaining, windows);
                if (!ReferenceEquals(next, array[index]))
                {
                    array[index] = next;
                }
            }
        }
        if (node is JsonObject obj && type.Properties is not null)
        {
            foreach ((string name, ValueTypeDefinition property) in type.Properties)
            {
                if (obj.TryGetPropertyValue(name, out JsonNode? current))
                {
                    JsonNode? next = Normalize(current, property, depth + 1, ref remaining, windows);
                    if (!ReferenceEquals(next, current))
                    {
                        obj[name] = next;
                    }
                }
                else
                {
                    if (!property.Optional)
                    {
                        throw new FormatException("path.propertyRequired");
                    }
                }
            }
        }
        if (node is JsonObject mapping && type.ValueType is { } valueType)
        {
            foreach (string key in mapping.Select(property => property.Key).ToArray())
            {
                JsonNode? current = mapping[key];
                JsonNode? next = Normalize(current, type.Properties?.GetValueOrDefault(key) ?? valueType, depth + 1, ref remaining, windows);
                if (!ReferenceEquals(current, next))
                {
                    mapping[key] = next;
                }
            }
        }
        return node;
    }

    /// <summary>Path・Int64・辞書の補足を重ね、他の型はJSONからの推測を維持します。</summary>
    /// <returns>実際に存在する構造へ重ねた型です。</returns>
    internal static ValueTypeDefinition? Overlay(ValueTypeDefinition? inferred, ValueTypeDefinition? metadata, int depth)
    {
        if (metadata is null || 32 < depth)
        {
            return inferred;
        }
        bool scalar = metadata.Type is "path" or "int64";
        if (scalar)
        {
            return metadata;
        }
        Dictionary<string, ValueTypeDefinition>? properties = null;
        if (metadata.Properties is not null || metadata.ValueType is not null)
        {
            properties = new(inferred?.Properties ?? new Dictionary<string, ValueTypeDefinition>(), StringComparer.Ordinal);
            foreach (string key in properties.Keys.ToArray())
            {
                properties[key] = Overlay(properties[key], metadata.Properties?.GetValueOrDefault(key) ?? metadata.ValueType, depth + 1)!;
            }
            foreach ((string key, ValueTypeDefinition value) in metadata.Properties ?? new Dictionary<string, ValueTypeDefinition>())
            {
                if (!properties.ContainsKey(key))
                {
                    properties[key] = Overlay(null, value, depth + 1)!;
                }
            }
        }
        return (inferred ?? metadata) with { ElementType = Overlay(inferred?.ElementType, metadata.ElementType, depth + 1), Properties = properties ?? inferred?.Properties,
            KeyType = metadata.KeyType ?? inferred?.KeyType, ValueType = metadata.ValueType ?? inferred?.ValueType };
    }
}
