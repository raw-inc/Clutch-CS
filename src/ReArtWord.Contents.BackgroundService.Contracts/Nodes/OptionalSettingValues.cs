using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>省略を許可した任意設定だけ、JSON nullを未指定へ正規化します。</summary>
public static class OptionalSettingValues
{
    /// <summary>既存のnullable設定とは別に、省略の意味を宣言するスキーマ拡張です。</summary>
    public static string Marker => "x-omit-if-null";

    /// <summary>必須・条件付き必須や未知の項目を省略可能へ読み替えません。</summary>
    /// <returns>欠落・nullを省略できる明示された任意設定かです。</returns>
    public static bool CanOmit(JsonElement schema, string? property)
    {
        string properties = "properties", required = "required", conditional = "x-requiredWhen";
        return property is not null && schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty(properties, out JsonElement fields) && fields.TryGetProperty(property, out JsonElement field)
            && field.ValueKind == JsonValueKind.Object && field.TryGetProperty(Marker, out JsonElement marker) && marker.ValueKind == JsonValueKind.True
            && !field.TryGetProperty(conditional, out _)
            && (!schema.TryGetProperty(required, out JsonElement mandatory) || !mandatory.EnumerateArray().Any(item => string.Equals(item.GetString(), property, StringComparison.Ordinal)));
    }

    /// <summary>未指定の任意設定を除き、ゼロ・false・空文字や必須設定を保持します。</summary>
    /// <returns>省略対象がある場合だけ複製した設定です。</returns>
    public static JsonElement Normalize(JsonElement settings, JsonElement schema)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Null && CanOmit(schema, property.Name)))
        {
            return settings;
        }
        System.Buffers.ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { MaxDepth = ContractJson.Options.MaxDepth }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in settings.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Null || !CanOmit(schema, property.Name))
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = ContractJson.Options.MaxDepth });
        return document.RootElement.Clone();
    }
}
