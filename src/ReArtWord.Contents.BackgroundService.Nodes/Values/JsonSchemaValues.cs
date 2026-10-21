using System.Text.Json;
using Json.Schema;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>元のJSONスキーマを検証に使用し、外部参照の取得や無制限のキャッシュを行いません。</summary>
// 抑制理由: 外部ライブラリーの例外を、既存ノードが扱う型検証失敗へ境界で翻訳します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW333", Justification = "JSONスキーマ評価の例外を共通のFormatExceptionへ翻訳する境界です。") ]
internal static class JsonSchemaValues
{
    private static readonly Lock gate = new();
    private static readonly Dictionary<string, JsonSchema> schemas = new(StringComparer.Ordinal);

    /// <summary>構文・参照を確認した有限キャッシュのスキーマを取得します。</summary>
    /// <returns>型ヒントではなく全制約を持つ検証器です。</returns>
    internal static JsonSchema Read(JsonElement schema)
    {
        string text = schema.GetRawText();
        if (65536 < text.Length)
        {
            throw new FormatException("schema.size");
        }
        using (gate.EnterScope())
        {
            if (schemas.TryGetValue(text, out JsonSchema? cached))
            {
                return cached;
            }
            try
            {
                JsonSchema parsed = JsonSchema.Build(schema, new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => null } });
                if (16 <= schemas.Count)
                {
                    schemas.Remove(schemas.Keys.First());
                }
                schemas.Add(text, parsed);
                return parsed;
            }
            catch (Exception exception) when (exception is JsonSchemaException or ArgumentException or InvalidOperationException)
            {
                throw new FormatException("schema.definition", exception);
            }
        }
    }

    /// <summary>同じ要素スキーマを一度だけ取得し、反復開始前に全件を評価します。</summary>
    internal static void ValidateItems(JsonElement array, JsonElement schema)
    {
        try
        {
            JsonSchema compiled = Read(schema);
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (!compiled.Evaluate(item).IsValid)
                {
                    throw new FormatException("schema.value");
                }
            }
        }
        catch (Exception exception) when (exception is JsonSchemaException or ArgumentException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw new FormatException("schema.evaluation", exception);
        }
    }

    /// <summary>必須・範囲・追加プロパティ等を表示用の推測型へ縮約せず評価します。</summary>
    internal static void Validate(JsonElement value, JsonElement schema)
    {
        try
        {
            if (!Read(schema).Evaluate(value).IsValid)
            {
                throw new FormatException("schema.value");
            }
        }
        catch (Exception exception) when (exception is JsonSchemaException or ArgumentException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw new FormatException("schema.evaluation", exception);
        }
    }
}
