using System.Buffers.Binary;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Desktop;

/// <summary>RTCP sender reportでRTPのrandom offsetとwrapを解き、取得時刻を復元します。</summary>
public sealed class DesktopRtpClock
{
    private uint source;
    private uint anchorTimestamp;
    private long anchorUnixMicroseconds;
    private bool initialized;
    private bool awaitingKeyFrame = true;

    /// <summary>同じsocketへ送られたRTCP SRを登録します。</summary>
    /// <returns>有効なsender reportか。</returns>
    public bool Observe(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 28 || packet[0] >> 6 != 2 || packet[1] != 200 || BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) < 6)
        {
            return false;
        }
        long seconds = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        uint fraction = BinaryPrimitives.ReadUInt32BigEndian(packet[12..]);
        source = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        anchorTimestamp = BinaryPrimitives.ReadUInt32BigEndian(packet[16..]);
        anchorUnixMicroseconds = checked((seconds - 2_208_988_800L) * 1_000_000L + (long)(((ulong)fraction * 1_000_000) >> 32));
        initialized = true;
        return true;
    }

    /// <summary>90kHzのwrapを符号付き差へ変換し、音声と共通の単調時計へ戻します。</summary>
    /// <returns>取得側の単調時計のmicrosecondsです。SR未取得時はnullです。</returns>
    public long? Map(ReadOnlySpan<byte> packet, long nowUnixMicroseconds, long nowMonotonicMicroseconds)
    {
        if (!initialized || packet.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(packet[8..]) != source)
        {
            return null;
        }
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        int delta = unchecked((int)(timestamp - anchorTimestamp));
        long unixMicroseconds = anchorUnixMicroseconds + delta * 1_000_000L / 90_000;

        // NTPの2036年era wrapは、受信時刻に最も近いeraへ戻します。
        long era = 4_294_967_296L * 1_000_000L;
        unixMicroseconds += (long)Math.Round((nowUnixMicroseconds - unixMicroseconds) / (double)era) * era;
        return nowMonotonicMicroseconds + unixMicroseconds - nowUnixMicroseconds;
    }

    /// <summary>時刻未確定・滞留フレームを公開せず、次の同期済みIDRで復帰します。</summary>
    /// <returns>送信可能な取得時刻です。破棄するフレームではnullです。</returns>
    public long? MapFrame(ReadOnlySpan<byte> packet, long nowUnixMicroseconds, long nowMonotonicMicroseconds, bool keyFrame)
    {
        long? captured = Map(packet, nowUnixMicroseconds, nowMonotonicMicroseconds);
        if (captured is null || captured < 0 || 5_000_000 < Math.Abs(nowMonotonicMicroseconds - captured.Value))
        {
            awaitingKeyFrame = true;
            return null;
        }
        if (awaitingKeyFrame && !keyFrame)
        {
            return null;
        }
        awaitingKeyFrame = false;
        return captured;
    }
}
