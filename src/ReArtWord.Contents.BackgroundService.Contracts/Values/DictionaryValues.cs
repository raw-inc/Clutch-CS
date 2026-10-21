using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>Key-Value配列から一意な辞書を組み立て、型と反復時の組を保持します。</summary>
public static class DictionaryValues
{
    /// <summary>辞書構築ノードの識別子です。</summary>
    public static string NodeType => "value.dictionary";

    /// <summary>同じキーの再入力を既定ではエラーにします。</summary>
    public static string SettingsSchema => """
        {"type":"object","properties":{"duplicate":{"x-runtime-input":false,"type":"string","enum":["reject","replace"],"default":"reject","description":"Reject duplicate keys, or replace with the last supplied value. Keys are case-sensitive."}}}
        """;

    /// <summary>入力済み辞書またはKey-Value配列へ一組を追加します。入力は変更しません。</summary>
    /// <returns>全キーが一意で、共通の値型を補った辞書です。</returns>
    public static NodeValue Build(NodeValue? items, NodeValue? item, string duplicate = "reject")
        => Collect(Entries(items, item), duplicate, new("string"));

    /// <summary>各行の型を一度ずつ読み、反復の結果を二乗コストで再構築しません。</summary>
    /// <returns>重複方針を適用した辞書です。</returns>
    public static NodeValue Collect(IEnumerable<NodeValue> entries, string duplicate, ValueTypeDefinition keyType)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(keyType);
        bool replace = duplicate is "replace";
        bool supported = duplicate is "reject" or "replace";
        if (!supported)
        {
            throw new FormatException("dictionary.duplicatePolicy");
        }
        JsonObject result = [];
        Dictionary<string, ValueTypeDefinition> types = new(StringComparer.Ordinal);
        foreach (NodeValue source in entries)
        {
            if (source.Kind != EValueKind.Json || source.Json.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("dictionary.entry.object");
            }
            foreach (JsonProperty property in source.Json.EnumerateObject())
            {
                _ = DictionaryKeys.Read(property.Name, keyType);
                if (!replace && result.ContainsKey(property.Name))
                {
                    throw new FormatException("key.duplicate");
                }
                NodeValue value = NodeValue.FromJson(property.Value) with { DataType = PathTypeMetadata.Copy(ValueTypeInference.Select(source.DataType, PropertyPorts.Name(string.Empty, property.Name))) };
                result[property.Name] = JsonNode.Parse(value.Json.GetRawText());
                types[property.Name] = PathTypeMetadata.TypeOf(value) ?? new("any", Optional: true);
            }
        }
        ValueTypeDefinition type = new("object", Properties: types, KeyType: keyType, ValueType: Common(types.Values));
        return PathTypeMetadata.Apply(NodeValue.FromJson(JsonSerializer.SerializeToElement(result)), type);
    }

    /// <summary>一括入力と追加一件を、型情報を保持した同じ列へ揃えます。</summary>
    /// <returns>入力順のKey-Valueです。</returns>
    private static IEnumerable<NodeValue> Entries(NodeValue? items, NodeValue? item)
    {
        if (items is not null)
        {
            if (items.Kind == EValueKind.Json && items.Json.ValueKind == JsonValueKind.Array)
            {
                for (int index = 0; index < items.Json.GetArrayLength(); index++)
                {
                    yield return JsonPointer.Select(items, "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                yield return items;
            }
        }
        if (item is not null)
        {
            yield return item;
        }
    }

    /// <summary>異なる値型を変換せずanyにし、nullだけは省略可能性に合流します。</summary>
    /// <returns>空辞書も表せる共通の値型です。</returns>
    public static ValueTypeDefinition Common(IEnumerable<ValueTypeDefinition> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        ValueTypeDefinition? result = null;
        bool nullable = false;
        foreach (ValueTypeDefinition type in types)
        {
            bool nullableAny = type is { Type: "any", Optional: true };
            if (nullableAny)
            {
                nullable = true;
                continue;
            }
            result = result is null ? type
                                    : InferredArrayTypes.Merge(result, type) ?? new("any");
        }
        return (result ?? new("any")) with { Optional = nullable || result?.Optional == true };
    }

    /// <summary>辞書の各項目を、Key-Valueノードと同じ一項目Objectの配列へ変換します。</summary>
    /// <returns>可能な範囲で挿入順を保つ、独立した反復値です。</returns>
    public static NodeValue ForEach(NodeValue dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        if (dictionary.Kind != EValueKind.Json || dictionary.Json.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("dictionary.object");
        }
        JsonArray entries = [];
        foreach (JsonProperty property in dictionary.Json.EnumerateObject())
        {
            entries.Add(new JsonObject { [property.Name] = JsonNode.Parse(property.Value.GetRawText()) });
        }
        return PathTypeMetadata.Apply(NodeValue.FromJson(JsonSerializer.SerializeToElement(entries)), new("array", ItemType(PathTypeMetadata.TypeOf(dictionary))));
    }

    /// <summary>一組を選ぶ場合の型を、反復・抽選・添字選択で共通に扱います。</summary>
    /// <returns>任意のキー一つとその値を保持する型です。</returns>
    public static ValueTypeDefinition ItemType(ValueTypeDefinition? type) => new("object",
        Properties: type?.Properties?.ToDictionary(pair => pair.Key, pair => pair.Value with { Optional = true }, StringComparer.Ordinal),
        KeyType: type?.KeyType ?? new("string"), ValueType: type?.ValueType ?? Common(type?.Properties?.Values ?? []));

    /// <summary>辞書全体を配列へ複製せず、指定位置の一組だけを取り出します。</summary>
    /// <returns>キー・値の対応と型情報を保持したKey-Valueです。</returns>
    public static NodeValue Select(NodeValue dictionary, int index)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        if (dictionary.Kind != EValueKind.Json || dictionary.Json.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("dictionary.object");
        }
        if (index < 0 || dictionary.Json.EnumerateObject().Count() <= index)
        {
            throw new FormatException("pick.index.range");
        }
        JsonProperty property = dictionary.Json.EnumerateObject().ElementAt(index);
        NodeValue selected = JsonPointer.Select(dictionary, PropertyPorts.Name(string.Empty, property.Name));
        ValueTypeDefinition value = PathTypeMetadata.TypeOf(selected) ?? new("any");
        ValueTypeDefinition entry = new("object", Properties: new Dictionary<string, ValueTypeDefinition> { [property.Name] = value },
            KeyType: dictionary.DataType?.KeyType ?? new("string"), ValueType: value);
        return PathTypeMetadata.Apply(NodeValue.FromObject(new Dictionary<string, JsonElement> { [property.Name] = property.Value }), entry);
    }
}
