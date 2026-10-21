using System.Buffers.Binary;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Streams;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Terminal;

/// <summary>端末のJSONフレームを長さ付きで運び、読み書きの領域を再利用します。</summary>
public static class TerminalWire
{
    /// <summary>64KiBのBase64出力と状態を格納できる最大フレーム長です。</summary>
    public static int MaximumMessageBytes => 128 * 1024;

    /// <summary>先頭4バイトと本文を呼出側が所有する有限バッファへ読みます。</summary>
    /// <returns>本文の長さです。</returns>
    public static async Task<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int length = await ReadOrEndAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (length == 0)
        {
            throw new EndOfStreamException("The terminal connection ended.");
        }
        return length;
    }

    /// <summary>次のフレームがない正常切断だけを0で返し、途中切断は失敗として扱います。</summary>
    /// <returns>本文の長さ、または正常切断の0です。</returns>
    public static async Task<int> ReadOrEndAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!await FrameReader.ReadHeaderAsync(stream, buffer[..4], cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(buffer.Span);
        if (length < 1 || MaximumMessageBytes < length || buffer.Length - 4 < length)
        {
            throw new InvalidDataException("Invalid terminal frame length.");
        }
        await stream.ReadExactlyAsync(buffer.Slice(4, length), cancellationToken).ConfigureAwait(false);
        return length;
    }
}
