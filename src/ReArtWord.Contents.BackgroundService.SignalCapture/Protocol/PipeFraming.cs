using System.Buffers.Binary;
using System.IO;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>両方向の長さ付きバイナリフレームを扱います。</summary>
internal static class PipeFraming
{
    /// <summary>kind を含むフレーム長の上限。</summary>
    internal static int MaximumLength => 2 * 1024 * 1024;

    /// <summary>途中 EOF と正常 EOF を区別し、長さ検証後に割り当てます。</summary>
    /// <returns>正常 EOF では null。</returns>
    internal static async Task<PipeFrame?> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var lengthBytes = new byte[4];
        int first = await input.ReadAsync(lengthBytes.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }
        await input.ReadExactlyAsync(lengthBytes.AsMemory(1), cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length is < 1 || MaximumLength < length)
        {
            throw new InvalidDataException("Invalid pipe frame length.");
        }
        var bytes = new byte[length];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return new PipeFrame(bytes[0], bytes.AsSpan(1).ToArray());
    }

    /// <summary>制御フレームを構成します。</summary>
    /// <returns>ヘッダーを含む送信バイト列。</returns>
    internal static byte[] Control(byte[] json)
    {
        var frame = new byte[5 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, 1 + json.Length);
        frame[4] = 1;
        json.CopyTo(frame, 5);
        return frame;
    }

    /// <summary>メタデータと原本を連結し、一度の書き込みで送ります。</summary>
    /// <returns>kind 2 の送信バイト列。</returns>
    internal static byte[] Packet(byte[] json, byte[] raw)
    {
        int length = 5 + json.Length + raw.Length;
        if (MaximumLength < length)
        {
            throw new InvalidDataException("Packet metadata is too large.");
        }
        var frame = new byte[4 + length];
        BinaryPrimitives.WriteInt32BigEndian(frame, length);
        frame[4] = 2;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), json.Length);
        json.CopyTo(frame, 9);
        raw.CopyTo(frame, 9 + json.Length);
        return frame;
    }
}
