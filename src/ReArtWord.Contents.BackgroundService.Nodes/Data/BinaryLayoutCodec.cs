using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>明示したオフセットと型でバイト列を読み書きします。</summary>
// 抑制理由: 設定キーと操作名は既存ワークフローの互換性を維持する定数です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "設定キーと登録済み操作名は互換性を維持するプロトコル定数です。")]
internal static class BinaryLayoutCodec
{
    private const string FieldName = "name";
    private const string FieldBytes = "bytes";
    private const string FieldUtf8 = "utf8";
    private const string FieldLittle = "little";
    private const string FieldBig = "big";

    /// <summary>共通のフィールド定義スキーマを組み込みます。</summary>
    /// <returns>独立した設定スキーマです。</returns>
    internal static JsonElement AddFieldsSchema(JsonElement schema)
    {
        JsonObject result = JsonNode.Parse(schema.GetRawText())!.AsObject();
        JsonNode? condition = result["properties"]?["fields"]?["x-visibleWhen"]?.DeepClone();
        result["properties"]!["fields"] = JsonNode.Parse("""{"type":"array","items":{"type":"object","required":["name","type","offset"],"properties":{"name":{"type":"string","minLength":1},"type":{"type":"string","enum":["uint8","int8","uint16","int16","uint32","int32","uint64","int64","float32","float64","utf8","bytes"]},"offset":{"type":"integer","minimum":0,"maximum":2147483647},"length":{"type":"integer","minimum":0,"maximum":2147483647},"endian":{"type":"string","enum":["little","big"]}}}}""");
        if (condition is not null)
        {
            result["properties"]!["fields"]!["x-visibleWhen"] = condition;
        }
        return JsonSerializer.SerializeToElement(result);
    }

    /// <summary>可変長指定と重複名・範囲を検証します。</summary>
    /// <returns>フィールド設定の問題です。</returns>
    internal static IReadOnlyList<ValidationIssue> ValidateFields(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [];
        JsonElement fields = new NodeSettings(definition.Settings).Get("fields");
        if (fields.ValueKind != JsonValueKind.Array)
        {
            return issues;
        }
        HashSet<string> names = new(StringComparer.Ordinal);
        List<(int Start, int End)> ranges = [];
        foreach (JsonElement field in fields.EnumerateArray())
        {
            NodeSettings settings = new(field);
            try
            {
                int start = settings.ReadInt32("offset", -1);
                int length = Width(settings.ReadText("type")!, settings.ReadInt32("length", -1));
                int end = checked(start + length);
                string? name = settings.ReadText("name");
                if (start < 0 || length < 0 || string.IsNullOrEmpty(name) || !names.Add(name) || ranges.Any(range => start < range.End && range.Start < end))
                {
                    issues.Add(new ValidationIssue("binary.layout.invalid", "フィールド名・長さ・重複範囲を確認してください。", definition.Id, "fields"));
                }
                ranges.Add((start, end));
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                issues.Add(new ValidationIssue("binary.layout.invalid", "フィールド型とバイト範囲が不正です。", definition.Id, "fields"));
            }
        }
        return issues;
    }

    /// <summary>フィールド定義から構造化データを取り出します。</summary>
    /// <returns>定義したフィールドの値です。</returns>
    internal static NodeValue Decode(NodeValue input, JsonElement fields)
    {
        if (input.Kind != EValueKind.Binary)
        {
            throw new FormatException("Binary入力が必要です。");
        }
        if (fields.ValueKind == JsonValueKind.Undefined)
        {
            return input.Copy();
        }
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        Dictionary<string, ValueTypeDefinition> types = new(StringComparer.Ordinal);
        foreach (JsonElement field in fields.EnumerateArray())
        {
            NodeSettings settings = new(field);
            string name = settings.ReadText("name") ?? throw new FormatException("フィールド名が必要です。");
            string type = settings.ReadText("type") ?? throw new FormatException("フィールド型が必要です。");
            int offset = settings.ReadInt32("offset", -1);
            int length = Width(type, settings.ReadInt32("length", -1));
            if (offset < 0 || length < 0 || input.Binary.Length - length < offset)
            {
                throw new FormatException("フィールドが入力バイト列の範囲外です。");
            }
            values.Add(name, ReadField(input.Binary.Span.Slice(offset, length), type, settings.ReadText("endian", "little")!));
            string int64 = "int64";
            if (string.Equals(type, int64, StringComparison.Ordinal))
            {
                types.Add(name, new(int64));
            }
        }
        return PathTypeMetadata.Apply(NodeValue.FromObject(values), new("object", Properties: types));
    }

    /// <summary>フィールド定義に従いバイト列を構成します。</summary>
    /// <returns>生成した独立バイト列です。</returns>
    internal static NodeValue Encode(NodeValue input, JsonElement fields)
    {
        if (fields.ValueKind == JsonValueKind.Undefined && input.Kind == EValueKind.Binary)
        {
            return input.Copy();
        }
        if (input.Kind != EValueKind.Json || input.Json.ValueKind != JsonValueKind.Object || fields.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Binary構築にはオブジェクトとフィールド定義が必要です。");
        }
        int total = 0;
        foreach (JsonElement field in fields.EnumerateArray())
        {
            NodeSettings settings = new(field);
            int offset = settings.ReadInt32("offset", -1);
            int length = Width(settings.ReadText("type")!, settings.ReadInt32("length", -1));
            if (offset < 0 || length < 0)
            {
                throw new FormatException("フィールド範囲が不正です。");
            }
            total = Math.Max(total, checked(offset + length));
        }
        byte[] bytes = new byte[total];
        bool[] written = new bool[total];
        foreach (JsonElement field in fields.EnumerateArray())
        {
            NodeSettings settings = new(field);
            string type = settings.ReadText("type")!;
            int offset = settings.ReadInt32("offset");
            int length = Width(type, settings.ReadInt32("length", -1));
            if (!input.Json.TryGetProperty(settings.ReadText(FieldName)!, out JsonElement value))
            {
                throw new FormatException("構築するフィールド値がありません。");
            }
            if (written.AsSpan(offset, length).Contains(true))
            {
                throw new FormatException("出力フィールドの範囲が重複しています。");
            }
            WriteField(bytes.AsSpan(offset, length), type, settings.ReadText("endian", "little")!, value);
            written.AsSpan(offset, length).Fill(true);
        }
        return NodeValue.FromBytes(bytes);
    }

    /// <summary>フィールド型から必要バイト数を決定します。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static int Width(string type, int specified)
    {
        return type switch
        {
            "uint8" or "int8" => 1,
            "uint16" or "int16" => 2,
            "uint32" or "int32" or "float32" => 4,
            "uint64" or "int64" or "float64" => 8,
            "utf8" or "bytes" => specified,
            _ => throw new FormatException("未対応のフィールド型です。"),
        };
    }

    /// <summary>指定の型とバイト順で一値を読み取ります。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static object ReadField(ReadOnlySpan<byte> bytes, string type, string endian)
    {
        bool little = IsLittle(endian);
        byte[] ordered = bytes.ToArray();
        if (!little && !string.Equals(type, FieldBytes, StringComparison.Ordinal) && !string.Equals(type, FieldUtf8, StringComparison.Ordinal))
        {
            Array.Reverse(ordered);
        }
        return type switch
        {
            "uint8" => ordered[0],
            "int8" => unchecked((sbyte)ordered[0]),
            "uint16" => BinaryPrimitives.ReadUInt16LittleEndian(ordered),
            "int16" => BinaryPrimitives.ReadInt16LittleEndian(ordered),
            "uint32" => BinaryPrimitives.ReadUInt32LittleEndian(ordered),
            "int32" => BinaryPrimitives.ReadInt32LittleEndian(ordered),
            "uint64" => BinaryPrimitives.ReadUInt64LittleEndian(ordered),
            "int64" => BinaryPrimitives.ReadInt64LittleEndian(ordered),
            "float32" => BinaryPrimitives.ReadSingleLittleEndian(ordered),
            "float64" => BinaryPrimitives.ReadDoubleLittleEndian(ordered),
            "utf8" => new UTF8Encoding(false, true).GetString(bytes),
            "bytes" => ordered.Select(item => (int)item).ToArray(),
            _ => throw new FormatException("未対応のフィールド型です。"),
        };
    }

    /// <summary>指定の型とバイト順で一値を書き込みます。</summary>
    private static void WriteField(Span<byte> bytes, string type, string endian, JsonElement value)
    {
        bool little = IsLittle(endian);
        switch (type)
        {
            case "uint8": bytes[0] = value.GetByte(); break;
            case "int8": bytes[0] = unchecked((byte)value.GetSByte()); break;
            case "uint16": BinaryPrimitives.WriteUInt16LittleEndian(bytes, value.GetUInt16()); break;
            case "int16": BinaryPrimitives.WriteInt16LittleEndian(bytes, value.GetInt16()); break;
            case "uint32": BinaryPrimitives.WriteUInt32LittleEndian(bytes, value.GetUInt32()); break;
            case "int32": BinaryPrimitives.WriteInt32LittleEndian(bytes, value.GetInt32()); break;
            case "uint64": BinaryPrimitives.WriteUInt64LittleEndian(bytes, value.GetUInt64()); break;
            case "int64": BinaryPrimitives.WriteInt64LittleEndian(bytes, value.GetInt64()); break;
            case "float32": BinaryPrimitives.WriteSingleLittleEndian(bytes, value.GetSingle()); break;
            case "float64": BinaryPrimitives.WriteDoubleLittleEndian(bytes, value.GetDouble()); break;
            case "utf8":
                byte[] text = new UTF8Encoding(false, true).GetBytes(value.GetString()!);
                if (text.Length != bytes.Length)
                {
                    throw new FormatException("UTF8フィールド長と値のバイト長が一致しません。");
                }
                text.CopyTo(bytes);
                break;
            case "bytes":
                byte[] data = value.EnumerateArray().Select(item => item.GetByte()).ToArray();
                if (data.Length != bytes.Length)
                {
                    throw new FormatException("バイトフィールド長が一致しません。");
                }
                data.CopyTo(bytes);
                break;
            default:
                throw new FormatException("未対応のフィールド型です。");
        }
        if (!little && !string.Equals(type, FieldUtf8, StringComparison.Ordinal) && !string.Equals(type, FieldBytes, StringComparison.Ordinal))
        {
            bytes.Reverse();
        }
    }

    /// <summary>明示されたバイト順を検証します。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static bool IsLittle(string endian)
    {
        if (string.Equals(endian, FieldLittle, StringComparison.Ordinal))
        {
            return true;
        }
        if (string.Equals(endian, FieldBig, StringComparison.Ordinal))
        {
            return false;
        }
        throw new FormatException("バイト順はlittleまたはbigで指定してください。");
    }
}
