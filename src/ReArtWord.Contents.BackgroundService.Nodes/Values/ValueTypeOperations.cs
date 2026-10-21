using System.Buffers;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Data;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>値構築とHTTP入力で、明示型の変換・検証を一つに揃えます。</summary>
internal static class ValueTypeOperations
{
    /// <summary>実データの文字列を型名と誤認せず、明示型オブジェクトだけを読みます。</summary>
    /// <returns>検証済み型定義です。</returns>
    internal static ValueTypeDefinition ReadType(NodeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("type.descriptor.required");
        }
        CheckJson(value.Json, 0);
        ValueTypeDefinition result = value.Json.Deserialize<ValueTypeDefinition>(ContractJson.Options) ?? throw new FormatException("type.descriptor.required");
        if (result.Schema is { } schema)
        {
            result = JsonSchemaTypes.Read(schema) with { Optional = result.Optional };
        }
        ValidateType(result);
        return result;
    }

    /// <summary>型の構造と、指定されたJSONスキーマを検証します。</summary>
    internal static void ValidateType(ValueTypeDefinition type) => ValidateDefinition(type, 0);

    /// <summary>HTTP 照会と定数textを同じ精度・型規則で変換します。</summary>
    /// <returns>変換結果です。曖昧値や範囲外は例外です。</returns>
    internal static NodeValue ParseText(string text, ValueTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateType(type);
        if (1048576 < text.Length)
        {
            throw new FormatException("value.text.limit");
        }
        NodeValue value = type.Type switch
        {
            "string" => String(text),
            "path" => PathValues.Create(text),
            "char" => Character(text),
            "boolean" => NodeValue.FromObject(ConvertNode.Boolean(NodeValue.FromObject(text))),
            "byte" => NodeValue.FromObject(Byte(text)),
            "integer" or "int64" => NodeValue.FromObject(ScalarNumbers.Integer(ScalarNumbers.Token(NodeValue.FromObject(text)))),
            "number" => NodeValue.FromObject(ScalarNumbers.Parse(ScalarNumbers.Token(NodeValue.FromObject(text)))),
            "decimal" => NodeValue.FromObject(ExactDecimal.Parse(text)),
            "any" or "array" or "object" => Json(text),
            _ => throw new FormatException("type.unsupported"),
        };
        ValidateValue(value, type);
        return PathTypeMetadata.Apply(value, type);
    }

    /// <summary>JSONは元トークンを保持し、非JSONだけを文字列として扱います。</summary>
    /// <returns>暗黙の精度損失を起こさない値です。</returns>
    internal static NodeValue ParseAuto(string text)
    {
        NodeValue value;
        try
        {
            value = Json(text);
        }
        catch (JsonException)
        {
            return String(text);
        }
        CheckJson(value.Json, 0);
        return value;
    }

    /// <summary>既存JSON値は変換せず、指定型との一致だけを検証します。</summary>
    internal static void ValidateValue(NodeValue value, ValueTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateType(type);
        string any = "any";
        if (value.Kind == EValueKind.Binary)
        {
            if (!string.Equals(type.Type, any, StringComparison.Ordinal))
            {
                throw new FormatException("value.binary.type");
            }
            return;
        }
        ValidateJson(value.Json, type, 0);
        if (PathTypeMetadata.Copy(type) is not null)
        {
            _ = PathTypeMetadata.Apply(value, type);
        }
    }

    /// <summary>Unicode スカラー以外の壊れたUTF-16を置換保存しません。</summary>
    internal static void ValidateUnicode(string text) => TextValues.ValidateUnicode(text);

    /// <summary>型定義の再帰を有限にし、無関係なfieldsを受け入れません。</summary>
    private static void ValidateDefinition(ValueTypeDefinition type, int depth)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Schema is { } schema)
        {
            _ = JsonSchemaValues.Read(schema);
        }
        string array = "array";
        string obj = "object";
        if (32 < depth || !ValueTypeInference.KnownName(type.Type)
            || (type.ElementType is not null && !string.Equals(type.Type, array, StringComparison.Ordinal))
            || (type.Properties is not null && !string.Equals(type.Type, obj, StringComparison.Ordinal)))
        {
            throw new FormatException("type.definition.invalid");
        }
        if (type.ElementType is not null)
        {
            ValidateDefinition(type.ElementType, depth + 1);
        }
        if (type.KeyType is not null || type.ValueType is not null)
        {
            if (!string.Equals(type.Type, obj, StringComparison.Ordinal) || type.KeyType is null || type.ValueType is null || !DictionaryKeys.Supported(type.KeyType))
            {
                throw new FormatException("dictionary.type.invalid");
            }
            ValidateDefinition(type.ValueType, depth + 1);
        }
        if (type.Properties is not null)
        {
            if (256 < type.Properties.Count)
            {
                throw new FormatException("type.properties.limit");
            }
            foreach ((string key, ValueTypeDefinition property) in type.Properties)
            {
                ValidateUnicode(key);
                ValidateDefinition(property, depth + 1);
            }
        }
    }

    /// <summary>配列とオブジェクトの各値も同じ型規則で検証します。</summary>
    private static void ValidateJson(JsonElement value, ValueTypeDefinition type, int depth)
    {
        if (64 < depth)
        {
            throw new FormatException("value.depth");
        }
        if (type.Schema is { } schema)
        {
            JsonSchemaValues.Validate(value, schema);
            return;
        }
        if (value.ValueKind == JsonValueKind.Null && type.Optional)
        {
            return;
        }
        bool valid = type.Type switch
        {
            "any" => true,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "byte" or "integer" or "int64" or "number" or "decimal" => value.ValueKind == JsonValueKind.Number,
            "char" or "string" or "path" => value.ValueKind == JsonValueKind.String,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false,
        };
        if (!valid)
        {
            throw new FormatException("value.type.mismatch");
        }
        switch (type.Type)
        {
            case "byte": _ = Byte(value.GetRawText()); break;
            case "integer" or "int64": _ = ScalarNumbers.Integer(value.GetRawText()); break;
            case "number": _ = ScalarNumbers.Parse(value.GetRawText()); break;
            case "decimal": _ = ExactDecimal.Parse(value.GetRawText()); break;
            case "char": _ = Character(value.GetString()!); break;
            case "string": ValidateUnicode(value.GetString()!); break;
            case "path": _ = PathValues.Normalize(value.GetString()!); break;
            case "array":
                if (type.ElementType?.Schema is { } elementSchema)
                {
                    JsonSchemaValues.ValidateItems(value, elementSchema);
                    break;
                }
                foreach (JsonElement item in value.EnumerateArray())
                {
                    ValidateJson(item, type.ElementType ?? new("any"), depth + 1);
                }
                break;
            case "object":
                CheckJson(value, depth);
                if (type.KeyType is not null && type.ValueType is not null)
                {
                    foreach (JsonProperty property in value.EnumerateObject())
                    {
                        _ = DictionaryKeys.Read(property.Name, type.KeyType);
                        ValidateJson(property.Value, type.ValueType, depth + 1);
                    }
                }
                if (type.Properties is null)
                {
                    break;
                }
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (!type.Properties.TryGetValue(property.Name, out ValueTypeDefinition? propertyType))
                    {
                        throw new FormatException("value.property.unknown");
                    }
                    ValidateJson(property.Value, propertyType, depth + 1);
                }
                foreach ((string key, ValueTypeDefinition propertyType) in type.Properties)
                {
                    if (!propertyType.Optional && !value.TryGetProperty(key, out _))
                    {
                        throw new FormatException("value.property.required");
                    }
                }
                break;
            case "any": CheckJson(value, depth); break;
        }
    }

    /// <summary>生JSONでも範囲外の数値・重複キー・壊れた文字を認めません。</summary>
    private static void CheckJson(JsonElement value, int depth)
    {
        if (64 < depth)
        {
            throw new FormatException("value.depth");
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            _ = ScalarNumbers.Parse(value.GetRawText());
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            ValidateUnicode(value.GetString()!);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                CheckJson(item, depth + 1);
            }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                {
                    throw new FormatException("value.property.duplicate");
                }
                ValidateUnicode(property.Name);
                CheckJson(property.Value, depth + 1);
            }
        }
    }

    /// <summary>JSONの元数値トークンを維持した所有値を返します。</summary>
    /// <returns>複製されたJSONです。</returns>
    private static NodeValue Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return NodeValue.FromJson(document.RootElement);
    }

    /// <summary>置換文字を生成せず文字列を作ります。</summary>
    /// <returns>元の文字列値です。</returns>
    private static NodeValue String(string text)
    {
        ValidateUnicode(text);
        return NodeValue.FromObject(text);
    }

    /// <summary>一つのUnicode スカラーだけを文字として扱います。</summary>
    /// <returns>単一文字を含むJSON文字列です。</returns>
    private static NodeValue Character(string text)
    {
        if (Rune.DecodeFromUtf16(text.AsSpan(), out _, out int consumed) != OperationStatus.Done || consumed != text.Length)
        {
            throw new FormatException("char.scalar.required");
        }
        return NodeValue.FromObject(text);
    }

    /// <summary>整数の小数切捨てや範囲の折返しを行いません。</summary>
    /// <returns>0～255の値です。</returns>
    private static byte Byte(string text)
    {
        long value = ScalarNumbers.Integer(ScalarNumbers.Token(NodeValue.FromObject(text)));
        if (value < 0 || 255 < value)
        {
            throw new FormatException("byte.range");
        }
        return (byte)value;
    }
}
