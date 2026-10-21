using System.Security.Cryptography;
using System.Buffers;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>保存したJSON バイト列の整合性を固定長バッファーで検査します。</summary>
internal static class SqlitePayload
{
    /// <summary>保存行の破損を検査します。</summary>
    internal static void Verify(string payload, string hash)
    {
        if (!string.Equals(Hash(payload), hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("保存行の内容が破損しています。");
        }
    }

    /// <summary>UTF8保存行または圧縮ブロックを、大きな文字列やバイト配列へ復元せず検証します。</summary>
    internal static void Verify(Stream content, string hash)
    {
        Span<byte> digest = stackalloc byte[32];
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            using IncrementalHash checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int count;
            while ((count = content.Read(buffer, 0, buffer.Length)) != 0)
            {
                checksum.AppendData(buffer, 0, count);
            }
            _ = checksum.GetHashAndReset(digest);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (!string.Equals(Convert.ToHexString(digest), hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("保存行の内容が破損しています。");
        }
    }

    /// <summary>保存内容のUTF8 ハッシュを固定長バッファで計算し、大きな一時バイト配列を作りません。</summary>
    /// <returns>既存保存形式と同じSHA-256です。</returns>
    internal static string Hash(string payload)
    {
        Span<byte> buffer = stackalloc byte[4096];
        Span<byte> digest = stackalloc byte[32];
        if (payload.Length <= 1024)
        {
            int length = System.Text.Encoding.UTF8.GetBytes(payload, buffer);
            _ = SHA256.HashData(buffer[..length], digest);
        }
        else
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            System.Text.Encoder encoder = System.Text.Encoding.UTF8.GetEncoder();
            ReadOnlySpan<char> remaining = payload;
            bool completed;
            do
            {
                encoder.Convert(remaining, buffer, true, out int chars, out int bytes, out completed);
                hash.AppendData(buffer[..bytes]);
                remaining = remaining[chars..];
            }
            while (!completed);
            _ = hash.GetHashAndReset(digest);
        }
        return Convert.ToHexString(digest);
    }

}
