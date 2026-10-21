using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ZstdSharp;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>現在状態の基準点を検証し、履歴本文とは独立して圧縮します。</summary>
internal static class HistoryCheckpointCodec
{
    internal static int MaximumBytes => 268435456;

    /// <summary>一つの整合した現在状態を有限の本文へ変換します。</summary>
    /// <returns>検証可能な非圧縮本文です。</returns>
    internal static byte[] Encode(RepositorySnapshot snapshot)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (MaximumBytes < bytes.Length)
        {
            throw new InvalidDataException("履歴基準点の容量が上限を超えています。");
        }
        return bytes;
    }

    /// <summary>保存済み本文をそのままzstdへ変換します。</summary>
    /// <returns>展開時に元の内容と照合する圧縮本文です。</returns>
    internal static byte[] Compress(byte[] payload)
    {
        using MemoryStream output = new();
        using (CompressionStream compression = new(output, leaveOpen: true))
        {
            compression.Write(payload);
        }
        return output.ToArray();
    }

    /// <summary>長さと完全ハッシュを検査してから復旧の基準点を読みます。</summary>
    /// <returns>検証できた一つの現在状態です。</returns>
    internal static RepositorySnapshot Decode(byte[] payload, int length, string hash, bool compressed)
    {
        if (length < 1 || MaximumBytes < length || MaximumBytes < payload.Length)
        {
            throw new InvalidDataException("履歴基準点の容量が不正です。");
        }
        byte[] raw = payload;
        if (compressed)
        {
            using MemoryStream input = new(payload, writable: false);
            using DecompressionStream expansion = new(input);
            raw = new byte[length];
            expansion.ReadExactly(raw);
            if (expansion.ReadByte() != -1)
            {
                throw new InvalidDataException("履歴基準点の展開容量が一致しません。");
            }
        }
        if (raw.Length != length || !string.Equals(Hash(raw), hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("履歴基準点の内容が一致しません。");
        }
        return JsonSerializer.Deserialize<RepositorySnapshot>(raw) ?? throw new InvalidDataException("履歴基準点がありません。");
    }

    /// <summary>圧縮形式によらない本文の検査値です。</summary>
    /// <returns>SHA-256の完全値です。</returns>
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
