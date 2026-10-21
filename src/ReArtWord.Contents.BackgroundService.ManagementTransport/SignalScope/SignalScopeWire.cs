using System.Buffers.Binary;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Streams;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.SignalScope;

/// <summary>長さ・種類付きのpipeフレームを解釈せず中継します。</summary>
public static class SignalScopeWire
{
    /// <summary>kindを含む本文の最大長です。</summary>
    public static int MaximumBytes => 2 * 1024 * 1024;

    /// <summary>完全な一件を再利用バッファへ読み込みます。</summary>
    /// <returns>先頭の長さ4バイトを含まない本文長です。</returns>
    public static async Task<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int length = await ReadOrEndAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (length == 0)
        {
            throw new EndOfStreamException("The capture connection ended.");
        }
        return length;
    }

    /// <summary>フレーム境界の正常切断と、ヘッダー・本文の欠落を区別します。</summary>
    /// <returns>本文の長さ、または正常切断の0です。</returns>
    public static async Task<int> ReadOrEndAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (buffer.Length < 5)
        {
            throw new ArgumentException("Capture frame buffer is too small.", nameof(buffer));
        }
        if (!await FrameReader.ReadHeaderAsync(stream, buffer[..4], cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(buffer.Span);
        if (length < 2 || MaximumBytes < length || buffer.Length - 4 < length)
        {
            throw new InvalidDataException("Invalid capture frame length.");
        }
        await stream.ReadExactlyAsync(buffer.Slice(4, length), cancellationToken).ConfigureAwait(false);
        if (buffer.Span[4] is not (1 or 2))
        {
            throw new InvalidDataException("Invalid capture frame kind.");
        }
        return length;
    }

    /// <summary>本文をログへ残さず制御フレームを作ります。</summary>
    /// <returns>長さ付きのJSONフレームです。</returns>
    public static byte[] Control(JsonElement value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        if (65536 < json.Length)
        {
            throw new InvalidDataException("Capture command exceeds its limit.");
        }
        byte[] frame = new byte[json.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame, json.Length + 1);
        frame[4] = 1;
        json.CopyTo(frame, 5);
        return frame;
    }
}
