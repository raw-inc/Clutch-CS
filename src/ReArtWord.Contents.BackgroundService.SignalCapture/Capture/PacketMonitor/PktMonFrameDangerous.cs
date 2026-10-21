using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.PacketMonitor;

/// <summary>ネイティブ受信記述子の範囲を検証し、寿命内に原本をコピーします。</summary>
internal static class PktMonFrameDangerous
{
    // ストリームのメタデータは隙間のない 40 bytes です。既定アラインメントの
    // C 構造体として扱うと時刻を 40 bytes 目のパケット本体から読んでしまいます。
    private static int metadataSize => 40;
    private static int timestampOffset => 32;
    private static readonly DateTimeOffset fileTimeEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly long maximumFileTime = DateTimeOffset.MaxValue.Ticks - fileTimeEpoch.Ticks;

    /// <summary>公開 ABI に従って受信フレームを読み取ります。</summary>
    /// <returns>独立した原本と、前回通知以降の欠落数。</returns>
    internal static (PacketRecord Packet, long Missed) ReadDangerous(nint descriptor)
    {
        (PacketRecord? packet, long missed) = ReadDangerous(descriptor, null);
        return (packet!, missed);
    }

    /// <summary>観測点を照合し、対象外の通知は原本のコピー前に除外します。</summary>
    /// <returns>受け入れた原本（除外時は null）と、除外の有無に依存しない欠落数。</returns>
    internal static (PacketRecord? Packet, long Missed) ReadDangerous(nint descriptor, PktMonSourceFilter? filter)
    {
        if (descriptor == 0)
        {
            throw new ArgumentException("PktMon descriptor is null.", nameof(descriptor));
        }

        nint data = Marshal.ReadIntPtr(descriptor);
        int size = Marshal.ReadInt32(descriptor, 8);
        int metadata = Marshal.ReadInt32(descriptor, 12);
        int offset = Marshal.ReadInt32(descriptor, 16);
        int length = Marshal.ReadInt32(descriptor, 20);
        long missed = (uint)Marshal.ReadInt32(descriptor, 24) + (long)(uint)Marshal.ReadInt32(descriptor, 28);
        if (data == 0 || size < metadataSize || 65536 < size || metadata < 0 || size - metadataSize < metadata || offset < 0 || length < 0 || 9000 < length || size - length < offset)
        {
            throw new ArgumentException("Packet Monitor returned an out-of-range data descriptor.", nameof(descriptor));
        }

        if (0 < length && offset < metadata + metadataSize && metadata < offset + length)
        {
            throw new ArgumentException("Packet Monitor metadata overlaps the packet data.", nameof(descriptor));
        }

        ushort componentId = (ushort)Marshal.ReadInt16(data, metadata + 16);
        ushort direction = (ushort)Marshal.ReadInt16(data, metadata + 12);
        uint dropReason = (uint)Marshal.ReadInt32(data, metadata + 22);
        uint dropLocation = (uint)Marshal.ReadInt32(data, metadata + 26);
        if (filter is not null && !filter.Accepts(componentId, direction, dropReason, dropLocation))
        {
            return (null, missed);
        }

        int kind = (ushort)Marshal.ReadInt16(data, metadata + 14);
        int link = kind switch
        {
            1 => 1,
            2 => 105,
            3 => 101,
            _ => 147
        };
        long timestamp = Marshal.ReadInt64(data, metadata + timestampOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(timestamp);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timestamp, maximumFileTime);

        // ローカル時刻への変換を挟むと FILETIME の上限付近で日時が丸められるため、UTC で加算します。
        DateTimeOffset time = fileTimeEpoch.AddTicks(timestamp);
        var bytes = new byte[length];
        Marshal.Copy(data + offset, bytes, 0, length);
        var packet = new PacketRecord
        {
            Data = bytes,
            LinkType = link,
            Time = time,
            OriginalLength = length,
            ObservationPoint = $"pktmon:{componentId}/{(ushort)Marshal.ReadInt16(data, metadata + 18)}/{direction}",
            Observation = $"PktMon Component={componentId} Edge={(ushort)Marshal.ReadInt16(data, metadata + 18)} Direction={direction} Group={(ulong)Marshal.ReadInt64(data, metadata)} Appearance={(ushort)Marshal.ReadInt16(data, metadata + 10)}"};
        return (packet, missed);
    }
}
