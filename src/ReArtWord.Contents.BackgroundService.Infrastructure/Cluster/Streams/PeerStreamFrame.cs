using System.Buffers.Binary;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Streams;

/// <summary>対応確認済みの要求接続だけで、長さの上位2bitへ受付区分を載せます。</summary>
internal static class PeerStreamFrame
{
    /// <summary>実処理や保存待ちで、元結果だけを読む照会の受付を塞ぎません。</summary>
    /// <returns>通常要求、保存・参照、元結果照会の区分です。</returns>
    internal static int Lane(string kind) => kind switch
    {
        "request" => 0,
        "receipt.query" or "query" or "prepare.query" or "replication.recovery" => 2,
        _ => 1,
    };

    /// <summary>従来と同じ4byteのprefixを使い、旧受付には区分を送信しません。</summary>
    /// <returns>本文を再送しない一回のフレーム送信です。</returns>
    internal static async Task WriteAsync(Stream stream, byte[] body, int lane, CancellationToken cancellationToken = default)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, body.Length | (lane << 30));
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}
