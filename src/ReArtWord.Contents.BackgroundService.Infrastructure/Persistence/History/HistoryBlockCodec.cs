using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ZstdSharp;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>一つの有限ブロックだけを圧縮・展開し、本文を復元してから既存の整合性を検査します。</summary>
internal static class HistoryBlockCodec
{
    internal static int MaximumBytes => 16777216;

    /// <summary>完成したzstd本文と、展開上限の検査に使う元サイズを返します。</summary>
    /// <returns>検証済みの圧縮本文・元サイズ・圧縮本文のチェックサムです。</returns>
    internal static (byte[] Data, int Length, string Hash) Encode(IReadOnlyList<HistoryRow> rows)
    {
        if (rows.Count is < 1 or > 10000)
        {
            throw new InvalidDataException("履歴ブロックの件数が範囲外です。");
        }
        long upperBound = 2;
        foreach (HistoryRow row in rows)
        {
            Validate(row);
            upperBound += EstimatedBytes(row);
            if (MaximumBytes < upperBound)
            {
                throw new InvalidDataException("履歴ブロックが展開上限を超えています。");
            }
            SqlitePayload.Verify(row.Payload, row.Hash);
        }
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(rows);
        if (MaximumBytes < raw.Length)
        {
            throw new InvalidDataException("履歴ブロックが展開上限を超えています。");
        }
        using MemoryStream output = new();
        using (CompressionStream compressor = new(output, leaveOpen: true))
        {
            compressor.Write(raw);
        }
        byte[] data = output.ToArray();
        return (data, raw.Length, Convert.ToHexString(SHA256.HashData(data)));
    }

    /// <summary>圧縮本文の破損と過大展開を拒否し、元の全行も検証します。</summary>
    /// <returns>一ブロック分の、元と同一の保存行です。</returns>
    internal static HistoryRow[] Decode(byte[] data, int length, string hash)
    {
        if (length < 1 || MaximumBytes < length || MaximumBytes < data.Length
            || !string.Equals(Convert.ToHexString(SHA256.HashData(data)), hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("履歴アーカイブの容量または内容が不正です。");
        }
        using MemoryStream source = new(data, writable: false);
        using DecompressionStream decompressor = new(source);
        byte[] raw = new byte[length];
        decompressor.ReadExactly(raw);
        if (decompressor.ReadByte() != -1)
        {
            throw new InvalidDataException("履歴アーカイブの展開サイズが一致しません。");
        }
        HistoryRow[] rows = JsonSerializer.Deserialize<HistoryRow[]>(raw) ?? throw new InvalidDataException("履歴アーカイブの本文がありません。");
        if (rows.Length is < 1 or > 10000)
        {
            throw new InvalidDataException("履歴アーカイブの件数が範囲外です。");
        }
        foreach (HistoryRow row in rows)
        {
            Validate(row);
            SqlitePayload.Verify(row.Payload, row.Hash);
        }
        return rows;
    }

    /// <summary>JSON escapeを含む最大保持量を、本文の追加コピーなしで見積もります。</summary>
    /// <returns>この行のJSON表現を収める保守的な上限です。</returns>
    internal static long EstimatedBytes(HistoryRow row) => 100L + 6L * ((long)row.Kind.Length + row.Key.Length + (row.Scope?.Length ?? 0) + row.Payload.Length + row.Hash.Length);

    /// <summary>破損した参照先を正常な未保存として返しません。</summary>
    private static void Validate(HistoryRow row)
    {
        if (row is null || string.IsNullOrEmpty(row.Kind) || string.IsNullOrEmpty(row.Key) || row.Payload is null || string.IsNullOrEmpty(row.Hash))
        {
            throw new InvalidDataException("履歴アーカイブの行形式が不正です。");
        }
    }
}
