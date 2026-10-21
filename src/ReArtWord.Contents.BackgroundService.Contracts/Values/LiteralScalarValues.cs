using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>実行を伴わず、元の表記を維持できる固定スカラーだけをプレビューします。</summary>
internal static class LiteralScalarValues
{
    private static readonly string[] literalTypes = ["auto", "any", "array", "object", "boolean", "byte", "integer", "int64", "number", "decimal"];

    /// <summary>値の変換結果が確定する型だけを読み取ります。</summary>
    /// <returns>静的な値、または実行時の検証が必要ならnullです。</returns>
    internal static NodeValue? Parse(string text, string? type)
    {
        string textType = "string";
        string automatic = "auto";
        string pathType = "path";
        if (string.Equals(type, pathType, StringComparison.Ordinal))
        {
            return PathValues.Create(text);
        }
        if (string.Equals(type, textType, StringComparison.Ordinal))
        {
            return NodeValue.FromObject(text);
        }
        if (type is null || !literalTypes.Contains(type, StringComparer.Ordinal))
        {
            return null;
        }
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text);
            bool allowed = type is "auto" or "any" || type switch
            {
                "array" => parsed.RootElement.ValueKind == JsonValueKind.Array,
                "object" => parsed.RootElement.ValueKind == JsonValueKind.Object,
                "boolean" => parsed.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "byte" => parsed.RootElement.ValueKind == JsonValueKind.Number && parsed.RootElement.TryGetByte(out byte number)
                    && string.Equals(parsed.RootElement.GetRawText(), JsonSerializer.Serialize(number), StringComparison.Ordinal),
                "integer" or "int64" => parsed.RootElement.ValueKind == JsonValueKind.Number && parsed.RootElement.TryGetInt64(out long number)
                    && string.Equals(parsed.RootElement.GetRawText(), JsonSerializer.Serialize(number), StringComparison.Ordinal),
                "number" => parsed.RootElement.ValueKind == JsonValueKind.Number && parsed.RootElement.TryGetDouble(out double number)
                    && double.IsFinite(number) && string.Equals(parsed.RootElement.GetRawText(), JsonSerializer.Serialize(number), StringComparison.Ordinal),
                "decimal" => parsed.RootElement.ValueKind == JsonValueKind.Number && parsed.RootElement.TryGetDecimal(out decimal number)
                    && string.Equals(parsed.RootElement.GetRawText(), JsonSerializer.Serialize(number), StringComparison.Ordinal),
                _ => false,
            };
            return allowed ? PathTypeMetadata.Apply(NodeValue.FromJson(parsed.RootElement), new(type))
                           : null;
        }
        catch (JsonException) when (string.Equals(type, automatic, StringComparison.Ordinal))
        {
            return NodeValue.FromObject(text);
        }
    }

}
