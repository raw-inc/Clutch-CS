using System.Buffers.Binary;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Desktop;

/// <summary>FFmpegの単一layer VP9 RTPを固定領域へ復元し、欠落後はkey frameを待ちます。</summary>
public sealed class Vp9RtpAssembler(Memory<byte> buffer) : IDesktopRtpAssembler
{
    private int length;
    private uint timestamp;
    private uint source;
    private ushort nextSequence;
    private bool started;
    private bool completed;
    private bool synchronized;

    /// <summary>次のAppendまで有効な完成フレームです。</summary>
    public ReadOnlyMemory<byte> Frame => buffer[..length];

    /// <summary>Profile 0のkey frameか。</summary>
    public bool KeyFrame
    {
        get; private set;
    }

    /// <summary>連続する単一layerのpacketだけを復元します。</summary>
    /// <returns>復号可能なフレームが完成したか。</returns>
    public bool Append(ReadOnlySpan<byte> packet)
    {
        if (!DesktopRtpPacket.TryPayload(packet, out ReadOnlySpan<byte> payload) || payload.Length < 2 || (payload[0] & ~0x0c) != 0)
        {
            started = synchronized = false;
            return false;
        }
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        uint current = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]), currentSource = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        bool first = (payload[0] & 8) != 0, last = (payload[0] & 4) != 0;
        if (first)
        {
            if (!started || !completed || sequence != nextSequence || currentSource != source)
            {
                synchronized = false;
            }

            // Profile 0のuncompressed headerだけを受理します。FFmpegのdescriptorにはP bitがありません。
            if ((payload[1] & 0xf8) != 0x80)
            {
                started = synchronized = false;
                return false;
            }
            KeyFrame = (payload[1] & 4) == 0;
            length = 0;
            timestamp = current;
            source = currentSource;
            started = true;
            completed = false;
        }
        else
        {
            if (!started || completed || sequence != nextSequence || timestamp != current || source != currentSource)
            {
                started = synchronized = false;
                return false;
            }
        }
        nextSequence = unchecked((ushort)(sequence + 1));
        if (last != ((packet[1] & 0x80) != 0) || buffer.Length - length < payload.Length - 1)
        {
            started = synchronized = false;
            return false;
        }
        payload[1..].CopyTo(buffer.Span[length..]);
        length += payload.Length - 1;
        completed = last;
        if (completed)
        {
            synchronized |= KeyFrame;
        }
        return completed && synchronized;
    }
}
