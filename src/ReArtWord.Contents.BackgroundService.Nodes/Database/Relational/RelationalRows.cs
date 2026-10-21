using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Database.Relational;

/// <summary>DBの行を有限のJSON配列へ変換し、列名衝突や未対応型を隠しません。</summary>
internal static class RelationalRows
{
    /// <summary>上限超過は読取りを失敗させ、部分配列を成功として返しません。</summary>
    /// <returns>入力順と正確な列値を持つ行です。</returns>
    internal static async Task<JsonArray> ReadAsync(DbDataReader reader, int maximumRows, int maximumBytes, CancellationToken cancellationToken = default)
    {
        string[] names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new FormatException("database.duplicateColumnsUseAliases");
        }
        JsonArray rows = [];
        long bytes = 2;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (maximumRows <= rows.Count)
            {
                throw new FormatException("database.rowLimit");
            }
            JsonObject row = new();
            for (int index = 0; index < names.Length; index++)
            {
                object value = await ReadValueAsync(reader, index, maximumBytes, cancellationToken).ConfigureAwait(false);
                row[names[index]] = Convert(value);
            }
            bytes += Encoding.UTF8.GetByteCount(row.ToJsonString()) + 1;
            if (maximumBytes < bytes)
            {
                throw new FormatException("database.resultLimit");
            }
            rows.Add(row);
        }
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new FormatException("database.oneResultSetRequired");
        }
        return rows;
    }

    /// <summary>大きな可変長の列を、上限検査前に全量確保しません。</summary>
    /// <returns>対応する一列の値です。</returns>
    private static async Task<object> ReadValueAsync(DbDataReader reader, int ordinal, int maximumBytes, CancellationToken cancellationToken = default)
    {
        if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
        {
            return DBNull.Value;
        }
        Type type = reader.GetFieldType(ordinal);
        if (type == typeof(string))
        {
            using TextReader text = reader.GetTextReader(ordinal);
            char[] buffer = new char[4096];
            StringBuilder value = new();
            int count;
            while ((count = await text.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (maximumBytes < (long)value.Length + count)
                {
                    throw new FormatException("database.resultLimit");
                }
                value.Append(buffer, 0, count);
            }
            return value.ToString();
        }
        if (type == typeof(byte[]))
        {
            using Stream stream = reader.GetStream(ordinal);
            using MemoryStream value = new();
            byte[] buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (maximumBytes < value.Length + count)
                {
                    throw new FormatException("database.resultLimit");
                }
                await value.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            return value.ToArray();
        }
        return reader.GetValue(ordinal);
    }

    /// <summary>値の意味を維持できる型だけをJSONへ写します。</summary>
    /// <returns>対応できないCLR型は明示失敗です。</returns>
    // 抑制理由: プロバイダーのDateTimeはoffsetを持たないことがあり、その意味を保持して出力します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "DBプロバイダーが返す日時を、タイムゾーンを捏造せずISO表記へ変換する境界です。")]
    private static JsonNode? Convert(object value) => value switch
    {
        DBNull => null,
        string text => JsonValue.Create(text),
        bool boolean => JsonValue.Create(boolean),
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal => JsonSerializer.SerializeToNode(value),
        float number when float.IsFinite(number) => JsonValue.Create(number),
        double number when double.IsFinite(number) => JsonValue.Create(number),
        byte[] binary => JsonValue.Create(System.Convert.ToBase64String(binary)),
        DateTimeOffset time => JsonValue.Create(time.ToString("O", CultureInfo.InvariantCulture)),
        DateTime time => JsonValue.Create(time.ToString("O", CultureInfo.InvariantCulture)),
        Guid id => JsonValue.Create(id.ToString("D")),
        _ => throw new FormatException("database.resultTypeUnsupported"),
    };
}
