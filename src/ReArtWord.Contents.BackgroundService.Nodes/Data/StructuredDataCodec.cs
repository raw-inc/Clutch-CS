using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Tomlyn;
using Tomlyn.Model;
using YamlDotNet.Serialization;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>標準ライブラリの解析器を使い形式ごとの値を変換します。</summary>
internal static class StructuredDataCodec
{
    private static readonly UTF8Encoding strictUtf8 = new(false, true);

    /// <summary>テキスト形式を構造化された値へ変換します。</summary>
    /// <returns>解析済みの値です。</returns>
    internal static NodeValue Parse(string format, NodeValue input)
    {
        string text = ReadText(input);
        return format switch
        {
            "json" => ParseJson(text),
            "yaml" => NodeValue.FromObject(Normalize(new DeserializerBuilder().WithMaximumRecursion(64).WithDuplicateKeyChecking().WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object?>(text))),
            "toml" => NodeValue.FromObject(Normalize(TomlSerializer.Deserialize<TomlTable>(text))),
            "csv" => NodeValue.FromObject(ParseCsv(text)),
            "text" => NodeValue.FromObject(text),
            _ => throw new FormatException("未対応のデータ形式です。"),
        };
    }

    /// <summary>構造化された値を指定形式へ変換します。</summary>
    /// <returns>テキストとしての出力です。</returns>
    internal static NodeValue Serialize(string format, NodeValue input)
    {
        if (input.Kind != EValueKind.Json)
        {
            throw new FormatException("テキスト形式のシリアライズにはJSON値が必要です。");
        }
        string text = format switch
        {
            "json" => input.Json.GetRawText(),
            "yaml" => new SerializerBuilder().Build().Serialize(ToObject(input.Json)),
            "toml" => SerializeToml(input.Json),
            "csv" => SerializeCsv(input.Json),
            "text" => input.GetString() ?? throw new FormatException("Textの入力は文字列で指定してください。"),
            _ => throw new FormatException("未対応のデータ形式です。"),
        };
        return NodeValue.FromObject(text);
    }

    /// <summary>厳密なUTF8またはJSON文字列を読み取ります。</summary>
    /// <returns>変換後の文字列です。</returns>
    internal static string ReadText(NodeValue input)
    {
        if (input.Kind == EValueKind.Binary)
        {
            return strictUtf8.GetString(input.Binary.Span);
        }
        return input.GetString() ?? throw new FormatException("文字列またはUTF8バイト列が必要です。");
    }

    /// <summary>文書を破棄しても利用できるJSON値を取得します。</summary>
    /// <returns>文書から独立した値です。</returns>
    private static NodeValue ParseJson(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return NodeValue.FromJson(document.RootElement);
    }

    /// <summary>数値精度を保ちながらシリアライザー用の値へ変換します。</summary>
    /// <returns>標準の辞書・配列・スカラーです。</returns>
    internal static object? ToObject(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                return value.EnumerateObject().ToDictionary(property => property.Name, property => ToObject(property.Value), StringComparer.Ordinal);
            case JsonValueKind.Array:
                return value.EnumerateArray().Select(ToObject).ToArray();
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                if (value.TryGetInt64(out long integer))
                {
                    return integer;
                }
                if (value.TryGetDecimal(out decimal exact))
                {
                    return exact;
                }
                throw new FormatException("この形式へ精度を保って変換できない数値です。");
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                throw new FormatException("未供給値はシリアライズできません。");
        }
    }

    /// <summary>TOMLルートが表であることを検証して書き出します。</summary>
    /// <returns>TOML文書です。</returns>
    private static string SerializeToml(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("TOML文書のルートには表が必要です。");
        }
        return TomlSerializer.Serialize((TomlTable)ToToml(value));
    }

    /// <summary>TOMLの表と配列モデルへ変換します。</summary>
    /// <returns>TOMLで表現できる値です。</returns>
    private static object ToToml(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            TomlTable table = new();
            foreach (JsonProperty property in value.EnumerateObject())
            {
                table.Add(property.Name, ToToml(property.Value));
            }
            return table;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            TomlArray array = new();
            foreach (JsonElement element in value.EnumerateArray())
            {
                array.Add(ToToml(element));
            }
            return array;
        }
        return ToObject(value) ?? throw new FormatException("TOMLにはnull値を出力できません。");
    }

    /// <summary>マッピングと列挙値をJSON互換の構造へ変換します。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static object? Normalize(object? value, int depth = 0)
    {
        if (64 < depth)
        {
            throw new FormatException("JSON互換変換の最大深さ64を超えています。");
        }
        if (value is IDictionary<string, object> table)
        {
            return table.ToDictionary(pair => pair.Key, pair => Normalize(pair.Value, depth + 1), StringComparer.Ordinal);
        }
        if (value is IDictionary dictionary)
        {
            Dictionary<string, object?> result = new(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                {
                    throw new FormatException("JSONへ変換するマッピングのキーは文字列で指定してください。");
                }
                result.Add(key, Normalize(entry.Value, depth + 1));
            }
            return result;
        }
        if (value is IEnumerable sequence && value is not string)
        {
            return sequence.Cast<object?>().Select(item => Normalize(item, depth + 1)).ToArray();
        }
        return value;
    }

    /// <summary>引用と改行を維持してCSV行を読み取ります。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static List<string[]> ParseCsv(string text)
    {
        using StringReader reader = new(text);
        using CsvParser parser = new(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            IgnoreBlankLines = false,
        });
        List<string[]> rows = [];
        while (parser.Read())
        {
            rows.Add(parser.Record!.ToArray());
        }
        return rows;
    }

    /// <summary>セル配列を引用規則に従いCSVへ書き込みます。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static string SerializeCsv(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("CSVは行配列を入力してください。");
        }
        using StringWriter writer = new(CultureInfo.InvariantCulture);
        using CsvWriter csv = new(writer, CultureInfo.InvariantCulture);
        foreach (JsonElement row in input.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("CSVの各行はセル配列で指定してください。");
            }
            foreach (JsonElement cell in row.EnumerateArray())
            {
                if (cell.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    throw new FormatException("CSVセルへ構造を暗黙変換できません。");
                }
                csv.WriteField(cell.ToString());
            }
            csv.NextRecord();
        }
        csv.Flush();
        return writer.ToString();
    }
}
