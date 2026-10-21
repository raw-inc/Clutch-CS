using System.Buffers.Binary;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Desktop;

/// <summary>ローカルエンコーダのRFC 6184/7798 packetを借用領域へ復元します。DONLは使用しません。</summary>
public sealed class H26xRtpAssembler : IDesktopRtpAssembler
{
    private readonly Memory<byte> buffer;
    private readonly bool hevc;
    private int length;
    private ushort nextSequence;
    private uint timestamp;
    private bool started;
    private bool fragment;
    private bool damaged;
    private bool keyFrame;
    private bool completed;
    private bool synchronized;
    private uint source;
    private ushort fragmentHeader;

    /// <summary>呼出元が寿命を所有する有限バッファを借用します。</summary>
    public H26xRtpAssembler(Memory<byte> buffer, bool hevc = false)
    {
        this.buffer = buffer;
        this.hevc = hevc;
    }

    /// <summary>最後に完成したAnnex Bの借用メモリです。次のAppendまで有効です。</summary>
    public ReadOnlyMemory<byte> Frame => buffer[..length];

    /// <summary>現在のaccess unitにIDR/IRAPが含まれるかです。</summary>
    public bool KeyFrame => keyFrame;

    /// <summary>markerまで揃ったフレームだけを公開します。欠落や不正packetは破棄します。</summary>
    /// <returns>新しいaccess unitを一つだけ復元したか。</returns>
    public bool Append(ReadOnlySpan<byte> packet)
    {
        if (!DesktopRtpPacket.TryPayload(packet, out ReadOnlySpan<byte> payload))
        {
            damaged = true;
            synchronized = false;
            return false;
        }
        uint current = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        uint currentSource = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (!started || current != timestamp || completed || source != currentSource)
        {
            if (damaged || fragment || (started && !completed) || source != currentSource)
            {
                synchronized = false;
            }
            length = 0;
            fragment = false;
            damaged = started && sequence != nextSequence;
            keyFrame = false;
            completed = false;
            timestamp = current;
            source = currentSource;
            started = true;
        }
        else
        {
            if (sequence != nextSequence)
            {
                damaged = true;
                synchronized = false;
            }
        }
        nextSequence = unchecked((ushort)(sequence + 1));
        if (damaged)
        {
            damaged = true;
            synchronized = false;
            return false;
        }
        damaged = !AppendPayload(payload);
        completed = (packet[1] & 0x80) != 0;
        if (damaged || (completed && fragment))
        {
            synchronized = false;
            return false;
        }
        if (completed)
        {
            synchronized |= keyFrame;
        }
        return completed && synchronized && length != 0;
    }

    /// <summary>AVCのsingle NAL/STAP-A/FU-AとHEVCのsingle NAL/AP/FUを受理します。</summary>
    /// <returns>全データが上限内で連続しているか。</returns>
    private bool AppendPayload(ReadOnlySpan<byte> payload)
    {
        int headerSize = hevc ? 2
                              : 1;
        if (payload.Length < headerSize)
        {
            return false;
        }
        int type = hevc ? (payload[0] >> 1) & 63
                        : payload[0] & 31;
        int aggregateType = hevc ? 48
                                 : 24;
        if (type < aggregateType)
        {
            return !fragment && Nal(payload);
        }
        if (type == aggregateType && !fragment)
        {
            if (payload.Length < headerSize + 2 + headerSize)
            {
                return false;
            }
            int offset = headerSize;
            while (offset + 2 <= payload.Length)
            {
                int size = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
                offset += 2;
                if (size == 0 || payload.Length - offset < size || !Nal(payload.Slice(offset, size)))
                {
                    return false;
                }
                offset += size;
            }
            return offset == payload.Length;
        }
        int fragmentType = hevc ? 49
                                : 28;
        if (type != fragmentType || payload.Length < headerSize + 2)
        {
            return false;
        }
        byte control = payload[headerSize];
        bool first = (control & 0x80) != 0;
        bool last = (control & 0x40) != 0;
        if ((!hevc && (control & 0x20) != 0) || (first && last) || first == fragment)
        {
            return false;
        }
        Span<byte> header = stackalloc byte[2];
        header[1] = 0;
        header[0] = (byte)((payload[0] & 0xe0) | (control & 31));
        if (hevc)
        {
            header[0] = (byte)((payload[0] & 0x81) | ((control & 63) << 1));
            header[1] = payload[1];
        }
        ushort identity = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (first)
        {
            fragmentHeader = identity;
            if (!Nal(header[..headerSize]))
            {
                return false;
            }
            fragment = true;
        }
        else
        {
            if (fragmentHeader != identity)
            {
                return false;
            }
        }
        int offsetBytes = headerSize + 1;
        if (buffer.Length - length < payload.Length - offsetBytes)
        {
            return false;
        }
        payload[offsetBytes..].CopyTo(buffer.Span[length..]);
        length += payload.Length - offsetBytes;
        if (last)
        {
            fragment = false;
        }
        return true;
    }

    /// <summary>start codeを付けて既存領域へ追記します。</summary>
    /// <returns>コピーできたか。</returns>
    private bool Nal(ReadOnlySpan<byte> nal)
    {
        int type = hevc ? (nal[0] >> 1) & 63
                        : nal[0] & 31;
        bool invalid = hevc ? nal.Length < 2 || (nal[1] & 7) == 0 || 47 < type
                            : type is < 1 or > 23;
        if (invalid || (nal[0] & 0x80) != 0 || buffer.Length - length < nal.Length + 4)
        {
            return false;
        }
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Span[length..], 1);
        length += 4;
        nal.CopyTo(buffer.Span[length..]);
        length += nal.Length;
        keyFrame |= hevc ? type is >= 16 and <= 21
                         : type == 5;
        return true;
    }
}
