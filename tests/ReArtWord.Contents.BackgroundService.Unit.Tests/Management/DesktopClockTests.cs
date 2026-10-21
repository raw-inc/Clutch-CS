using System.Buffers.Binary;
using ReArtWord.Contents.BackgroundService.Infrastructure.Desktop;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>音声のQPCと映像のRTCPを同じ単調時計へ対応させます。</summary>
public sealed class DesktopClockTests
{
    /// <summary>90kHz wrapを越えても取得時刻と単調時計の差を維持します。</summary>
    [Fact]
    public void RtpWrapAndRandomOffsetMapToTheCaptureClock()
    {
        const long unix = 1_700_000_000_000_000;
        DesktopRtpClock clock = new();
        byte[] report = Report(unix, uint.MaxValue - 44);
        Assert.True(clock.Observe(report));
        byte[] packet = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 45);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 42);
        Assert.Equal(900_001_000, clock.Map(packet, unix + 5000, 900_005_000));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 43);
        Assert.Null(clock.Map(packet, unix + 5000, 900_005_000));
    }

    /// <summary>2036年のNTP era wrapを受信時刻に対応させます。</summary>
    [Fact]
    public void NtpEraWrapIsResolvedWithoutAClockJump()
    {
        const long unix = 2_200_000_000_000_000;
        DesktopRtpClock clock = new();
        Assert.True(clock.Observe(Report(unix, 100)));
        byte[] packet = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 100);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 42);
        Assert.Equal(700_000, clock.Map(packet, unix + 2000, 702_000));
    }

    /// <summary>SRの遅着と古い映像では接続を失わず、確認済みIDRまで差分フレームを破棄します。</summary>
    [Fact]
    public void DelayedClockAndStaleFramesRecoverOnlyAtSynchronizedKeyFrames()
    {
        const long unix = 1_700_000_000_000_000;
        DesktopRtpClock clock = new();
        byte[] packet = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 100);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 42);
        Assert.Null(clock.MapFrame(packet, unix, 10_000_000, true));
        Assert.True(clock.Observe(Report(unix, 100)));
        Assert.Null(clock.MapFrame(packet, unix, 10_000_000, false));
        Assert.Equal(10_000_000, clock.MapFrame(packet, unix, 10_000_000, true));
        Assert.Null(clock.MapFrame(packet, unix + 6_000_000, 16_000_000, false));
        Assert.True(clock.Observe(Report(unix + 6_000_000, 100)));
        Assert.Null(clock.MapFrame(packet, unix + 6_000_000, 16_000_000, false));
        Assert.Equal(16_000_000, clock.MapFrame(packet, unix + 6_000_000, 16_000_000, true));
    }

    /// <summary>既知の取得時刻とRTP offsetを対応させたsender reportです。</summary>
    /// <returns>RFC3550 sender reportです。</returns>
    private static byte[] Report(long unix, uint rtp)
    {
        byte[] report = new byte[28];
        report[0] = 0x80;
        report[1] = 200;
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(4), 42);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(8), unchecked((uint)(unix / 1_000_000 + 2_208_988_800L)));
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(16), rtp);
        return report;
    }
}
