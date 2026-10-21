using System.Buffers.Binary;

namespace ReArtWord.Contents.BackgroundService.Runtime.Networking;

/// <summary>TCPの明示フレーム境界と上限を共通処理します。</summary>
public static class TcpFrame
{
    /// <summary>送信開始前にフレーム形式と禁止区切りを確認します。</summary>
    public static void ValidateWrite(byte[] bytes, string framing)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        switch (framing)
        {
            case "lengthPrefix":
            case "raw":
                return;
            case "line":
                if (bytes.Contains((byte)'\n'))
                {
                    throw new FormatException("lineフレームの本文に改行は含められません。");
                }
                return;
            default:
                throw new FormatException("TCP framingが不正です。");
        }
    }

    /// <summary>一つの明示フレームを送信します。</summary>
    /// <returns>送信完了です。</returns>
    public static async Task WriteAsync(Stream stream, byte[] bytes, string framing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateWrite(bytes, framing);
        switch (framing)
        {
            case "lengthPrefix":
                byte[] prefix = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
                await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                break;
            case "line":
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
                break;
            case "raw":
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new FormatException("TCP framingが不正です。");
        }
    }

    /// <summary>長さprefix、改行、または一readの分割データを取得します。</summary>
    /// <returns>一フレーム。入力終端のみnullです。</returns>
    public static Task<byte[]?> ReadAsync(Stream stream, string framing, int maximum, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ReadCoreAsync(stream, framing, maximum, cancellationToken);
    }

    /// <summary>一フレームを欠損なくバッファーへ取得します。</summary>
    /// <returns>一フレーム、または正常EOFのnullです。</returns>
    private static async Task<byte[]?> ReadCoreAsync(Stream stream, string framing, int maximum, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximum is < 1 or > 536870912)
        {
            throw new FormatException("TCP受信上限が不正です。");
        }
        switch (framing)
        {
            case "lengthPrefix":
                byte[] prefix = new byte[4];
                int first = await stream.ReadAsync(prefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (first == 0)
                {
                    return null;
                }
                await stream.ReadExactlyAsync(prefix.AsMemory(1), cancellationToken).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
                if (length < 0 || maximum < length)
                {
                    throw new FormatException("TCPフレーム長が不正です。");
                }
                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
                return body;
            case "line":
                using (MemoryStream line = new())
                {
                    byte[] single = new byte[1];
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            if (line.Length == 0)
                            {
                                return null;
                            }
                            throw new EndOfStreamException("TCP行が途中で切れました。");
                        }
                        if (single[0] == (byte)'\n')
                        {
                            return line.ToArray();
                        }
                        if (maximum <= line.Length)
                        {
                            throw new FormatException("TCP行の上限を超えました。");
                        }
                        line.WriteByte(single[0]);
                    }
                    throw new OperationCanceledException(cancellationToken);
                }
            case "raw":
                byte[] chunk = new byte[maximum];
                int received = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (received == 0)
                {
                    return null;
                }
                return chunk.AsSpan(0, received).ToArray();
            default:
                throw new FormatException("TCP framingが不正です。");
        }
    }
}
