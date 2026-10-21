using System.Buffers.Binary;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Streams;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.State;

/// <summary>状態観測の有限JSONフレームを内部TCP経路へ運びます。</summary>
public static class StateStreamWire
{
    /// <summary>購読指定と有限件数の表示値だけを扱う上限です。</summary>
    public static int MaximumBytes => 512 * 1024;

    /// <summary>正常EOFを0で返し、途中切断と長さ違反を拒否します。</summary>
    /// <returns>先頭4バイトに続く本文長です。</returns>
    public static async Task<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!await FrameReader.ReadHeaderAsync(stream, buffer[..4], cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(buffer.Span);
        if (length < 1 || MaximumBytes < length || buffer.Length - 4 < length)
        {
            throw new InvalidDataException("Invalid state observation frame.");
        }
        await stream.ReadExactlyAsync(buffer.Slice(4, length), cancellationToken).ConfigureAwait(false);
        return length;
    }

    /// <summary>一つの書き手からのみ使用する長さ付き送信です。</summary>
    /// <returns>本文までの送信完了です。</returns>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (value.Length < 1 || MaximumBytes < value.Length)
        {
            throw new InvalidDataException("Invalid state observation frame.");
        }
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, value.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(value, cancellationToken).ConfigureAwait(false);
    }
}
