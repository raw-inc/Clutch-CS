using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>Object配列とキー・値ペア配列の検索規則を、実行と型推論で共有します。</summary>
public static class KeyValueLookup
{
    /// <summary>検索ノードの公開識別子です。</summary>
    public static string NodeType => "data.keyValue.get";

    /// <summary>解釈が曖昧な入力も利用者が形式を固定できる設定です。</summary>
    public static string SettingsSchema => """
        {"type":"object","properties":{"format":{"type":"string","enum":["auto","objects","pairs"],"default":"auto","x-runtime-input":false,"description":"Automatic treats an object containing exactly key/value or Key/Value as a pair; all other objects are property maps. Object array accepts arrays made by Key and value nodes. Pair array requires string keys. Duplicate matching keys fail; null values are returned as null, not replaced by the default."}}}
        """;

    /// <summary>キーが存在しない場合だけC# nullを返し、JSON nullとの違いを維持します。</summary>
    /// <returns>選択値の独立した複製、またはキーの欠落です。</returns>
    public static NodeValue? Find(NodeValue? entries, NodeValue? key, string format = "auto")
    {
        if (key?.Kind != EValueKind.Json || key.Json.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("lookup.key.string");
        }
        string name = key.Json.GetString()!;
        string? selected = null;
        foreach ((string candidate, string pointer) in Entries(entries, format))
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                if (selected is not null)
                {
                    throw new FormatException("lookup.key.duplicate");
                }
                selected = pointer;
            }
        }
        return selected is null ? null
                                : JsonPointer.Select(entries!, selected);
    }

    /// <summary>本文を変換せず、検索対象キーと値の位置を一度ずつ列挙します。</summary>
    /// <returns>各形式で同じ意味を持つキーとJSON Pointerです。</returns>
    internal static IEnumerable<(string Key, string Pointer)> Entries(NodeValue? entries, string format)
    {
        if (entries?.Kind != EValueKind.Json || entries.Json.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            throw new FormatException("lookup.input.array");
        }
        bool automatic = format is "auto", pairs = format is "pairs";
        bool supported = format is "auto" or "objects" or "pairs";
        if (!supported)
        {
            throw new FormatException("lookup.format.invalid");
        }
        if (entries.Json.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in entries.Json.EnumerateObject())
            {
                yield return (property.Name, "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            }
            yield break;
        }
        int index = 0;
        foreach (JsonElement entry in entries.Json.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("lookup.entry.object");
            }
            string prefix = "/" + index.ToString(CultureInfo.InvariantCulture) + "/";
            (string Key, string Value)? fields = PairFields(entry);
            if (pairs || automatic && fields is not null)
            {
                if (fields is null || entry.GetProperty(fields.Value.Key).ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("lookup.entry.pair");
                }
                yield return (entry.GetProperty(fields.Value.Key).GetString()!, prefix + fields.Value.Value);
            }
            else
            {
                foreach (JsonProperty property in entry.EnumerateObject())
                {
                    yield return (property.Name, prefix + property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
                }
            }
            index++;
        }
    }

    /// <summary>ペアとして扱うのは予約された二項目だけを持つObjectです。</summary>
    /// <returns>小文字または.NET標準の大文字で揃ったペアの項目名です。</returns>
    private static (string Key, string Value)? PairFields(JsonElement entry)
    {
        string lowerKey = "key", lowerValue = "value", upperKey = "Key", upperValue = "Value";
        if (entry.EnumerateObject().Count() != 2)
        {
            return null;
        }
        if (entry.TryGetProperty(lowerKey, out _) && entry.TryGetProperty(lowerValue, out _))
        {
            return (lowerKey, lowerValue);
        }
        if (entry.TryGetProperty(upperKey, out _) && entry.TryGetProperty(upperValue, out _))
        {
            return (upperKey, upperValue);
        }
        return null;
    }
}
