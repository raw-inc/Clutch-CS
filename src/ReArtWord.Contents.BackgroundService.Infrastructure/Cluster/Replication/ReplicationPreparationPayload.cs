using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>同じ準備内容を一度だけ符号化し、展開上限と同一性を検証して元の保存処理へ渡します。</summary>
internal sealed record ReplicationPreparationPayload(JsonElement Plain, JsonElement? Compressed, int Length)
{
    internal static string Kind => "prepare.brotli";
    internal static string Capability => "replication.prepare-brotli.v1";

    /// <summary>十分に小さくなる大きな本文だけ、通常形式と併せて一回作成します。</summary>
    /// <returns>各固定対象へ再利用する準備本文です。</returns>
    internal static ReplicationPreparationPayload Create(ReplicationRequest request, int maximumBytes)
    {
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(request);
        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement plain = document.RootElement.Clone();
        if (raw.Length < 16384 || maximumBytes < raw.Length)
        {
            return new(plain, null, raw.Length);
        }
        using MemoryStream output = new();
        using (BrotliStream compressor = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(raw);
        }
        byte[] packed = JsonSerializer.SerializeToUtf8Bytes(new Envelope(request.Id, raw.Length, Convert.ToHexString(SHA256.HashData(raw)), output.ToArray()));
        if (raw.Length <= packed.Length + 512)
        {
            return new(plain, null, raw.Length);
        }
        using JsonDocument compressed = JsonDocument.Parse(packed);
        return new(plain, compressed.RootElement.Clone(), raw.Length);
    }

    /// <summary>長さ・終端・ハッシュ・操作IDを確認できた内容だけを受け入れます。</summary>
    /// <returns>通常のprepareと同じ要求です。</returns>
    internal static ReplicationRequest Read(JsonElement body, int maximumBytes)
    {
        Envelope packet = body.Deserialize<Envelope>() ?? throw new InvalidDataException("圧縮した準備要求がありません。");
        if (packet.Id == Guid.Empty || packet.Length < 1 || maximumBytes < packet.Length || packet.Data is null || maximumBytes < packet.Data.Length)
        {
            throw new InvalidDataException("圧縮した準備要求の上限または識別子が不正です。");
        }
        byte[] raw = new byte[packet.Length];
        if (!Decompress(packet.Data, raw) || !string.Equals(Convert.ToHexString(SHA256.HashData(raw)), packet.Hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("圧縮した準備要求の内容を確認できません。");
        }
        ReplicationRequest request = JsonSerializer.Deserialize<ReplicationRequest>(raw) ?? throw new InvalidDataException("準備要求がありません。");
        if (request.Id != packet.Id || request.Mutations is null)
        {
            throw new InvalidDataException("圧縮した準備要求の識別子が一致しません。");
        }
        return request;
    }

    /// <summary>可変なdecoderの状態を一つの実体で所有し、終端と全バイト消費を確認して解放します。</summary>
    /// <returns>過不足なく一つの本文を展開できたかです。</returns>
    private static bool Decompress(byte[] data, byte[] raw)
    {
        BrotliDecoder decoder = default;
        try
        {
            OperationStatus status = decoder.Decompress(data, raw, out int consumed, out int written);
            return status == OperationStatus.Done && consumed == data.Length && written == raw.Length;
        }
        finally
        {
            decoder.Dispose();
        }
    }

    /// <summary>圧縮前の大きさと完全性を、配送形式とは独立して固定します。</summary>
    private sealed record Envelope(Guid Id, int Length, string Hash, byte[] Data);
}
