using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>外部の型定義を、深さ・項目数・許可された構造に制限して読みます。</summary>
internal static class ValueTypeDescriptors
{
    /// <summary>小さな型契約の構成だけを読みます。</summary>
    /// <returns>不正・未知の定義はnullです。</returns>
    internal static ValueTypeDefinition? ReadDescriptor(JsonElement value, int depth, ref int remaining)
    {
        if (value.ValueKind != JsonValueKind.Object || 64 <= depth || --remaining < 0)
        {
            return null;
        }
        string typeKey = "type";
        string optionalKey = "optional";
        string schemaKey = "schema";
        string elementKey = "elementType";
        string propertiesKey = "properties";
        string keyTypeKey = "keyType", valueTypeKey = "valueType";
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (JsonProperty field in value.EnumerateObject())
        {
            bool allowed = string.Equals(field.Name, typeKey, StringComparison.Ordinal) || string.Equals(field.Name, optionalKey, StringComparison.Ordinal)
                || string.Equals(field.Name, elementKey, StringComparison.Ordinal) || string.Equals(field.Name, propertiesKey, StringComparison.Ordinal) || string.Equals(field.Name, schemaKey, StringComparison.Ordinal)
                || string.Equals(field.Name, keyTypeKey, StringComparison.Ordinal) || string.Equals(field.Name, valueTypeKey, StringComparison.Ordinal);
            if (!allowed || !keys.Add(field.Name) || 7 < keys.Count)
            {
                return null;
            }
        }
        if (!value.TryGetProperty(typeKey, out JsonElement name) || name.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string? type = name.GetString();
        if (type is null || !ValueTypeInference.KnownName(type))
        {
            return null;
        }
        bool hasOptional = value.TryGetProperty(optionalKey, out JsonElement option);
        if (hasOptional && option.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }
        bool optional = hasOptional && option.ValueKind == JsonValueKind.True;
        ValueTypeDefinition? element = null;
        if (value.TryGetProperty(elementKey, out JsonElement elementValue))
        {
            element = ReadDescriptor(elementValue, depth + 1, ref remaining);
            string array = "array";
            if (element is null || !string.Equals(type, array, StringComparison.Ordinal))
            {
                return null;
            }
        }
        IReadOnlyDictionary<string, ValueTypeDefinition>? properties = null;
        if (value.TryGetProperty(propertiesKey, out JsonElement propertyValue))
        {
            properties = ReadProperties(propertyValue, depth + 1, ref remaining);
            string obj = "object";
            if (properties is null || !string.Equals(type, obj, StringComparison.Ordinal))
            {
                return null;
            }
        }
        if (value.TryGetProperty(schemaKey, out JsonElement suppliedSchema))
        {
            if (suppliedSchema.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }
            return JsonSchemaTypes.Read(suppliedSchema) with { Optional = optional };
        }
        ValueTypeDefinition? keyType = null, valueType = null;
        if (keys.Contains(keyTypeKey) || keys.Contains(valueTypeKey))
        {
            string obj = "object";
            if (!string.Equals(type, obj, StringComparison.Ordinal) || !value.TryGetProperty(keyTypeKey, out JsonElement keyJson) || !value.TryGetProperty(valueTypeKey, out JsonElement valueJson)
                || (keyType = ReadDescriptor(keyJson, depth + 1, ref remaining)) is null || !DictionaryKeys.Supported(keyType)
                || (valueType = ReadDescriptor(valueJson, depth + 1, ref remaining)) is null)
            {
                return null;
            }
        }
        return new(type, element, properties, optional, KeyType: keyType, ValueType: valueType);
    }

    /// <summary>再帰的な型辞書の重複と走査上限を確認します。</summary>
    /// <returns>既知の辞書です。</returns>
    internal static Dictionary<string, ValueTypeDefinition>? ReadProperties(JsonElement value, int depth, ref int remaining)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        Dictionary<string, ValueTypeDefinition> result = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            ValueTypeDefinition? type = ReadDescriptor(property.Value, depth + 1, ref remaining);
            if (128 <= result.Count || type is null || !result.TryAdd(property.Name, type))
            {
                return null;
            }
        }
        return result;
    }
}
