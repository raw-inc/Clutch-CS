using System.Buffers.Binary;
using ReArtWord.Contents.BackgroundService.SignalCapture.Sending;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Packets.Tests;

/// <summary>L2・チェックサム差だけを正規化し、本文や通し番号の違いを残します。</summary>
public sealed class PacketFingerprintTests
{
    /// <summary>raw IPv4 と VLAN / padding 付き Ethernet の指紋が一致します。</summary>
    [Fact]
    public void NormalizesIpv4AcrossLinkLayersAndOffload()
    {
        byte[] ip = PacketComposer.UdpFrame("payload"u8.ToArray(), "192.0.2.1", "192.0.2.2", 9000);
        byte[] ethernet = new byte[18 + ip.Length + 20];
        ethernet[12] = 0x81;
        ethernet[15] = 100;
        ethernet[16] = 8;
        ip.CopyTo(ethernet, 18);
        ethernet[18 + 8] = 4;
        ethernet[18 + 10] = 0x12;
        ethernet[18 + 11] = 0x34;
        ethernet[18 + 26] = 0xab;
        ethernet[18 + 27] = 0xcd;
        Array.Fill(ethernet, (byte)0xee, 18 + ip.Length, 20);
        Assert.Equal(PacketFingerprint.Compute(ip, 101), PacketFingerprint.Compute(ethernet, 1));
        ethernet[^21] ^= 1;
        Assert.NotEqual(PacketFingerprint.Compute(ip, 101), PacketFingerprint.Compute(ethernet, 1));
    }

    /// <summary>TCP seq と IP ID は同一性に含みます。</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(24)]
    [InlineData(40)]
    public void PreservesTcpIdentityAndPayload(int offset)
    {
        byte[] ip = Capture.DemoCapture.Create(2).Data;
        byte[] changed = ip.ToArray();
        changed[offset] ^= 1;
        Assert.NotEqual(PacketFingerprint.Compute(ip, 101), PacketFingerprint.Compute(changed, 101));
    }

    /// <summary>IPv6 の拡張ヘッダー後の UDP チェックサムと hop limit を正規化します。</summary>
    [Fact]
    public void NormalizesIpv6ExtensionHeaderTransport()
    {
        var ip = new byte[60];
        ip[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), 20);
        ip[6] = 60;
        ip[7] = 64;
        ip[23] = 1;
        ip[39] = 2;
        ip[40] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(48), 1234);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(50), 5678);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(52), 12);
        ip[59] = 42;
        byte[] changed = ip.ToArray();
        changed[7] = 1;
        changed[54] = 0xfe;
        changed[55] = 0xff;
        Assert.Equal(PacketFingerprint.Compute(ip, 101), PacketFingerprint.Compute(changed, 101));
        changed[59]++;
        Assert.NotEqual(PacketFingerprint.Compute(ip, 101), PacketFingerprint.Compute(changed, 101));
    }

    /// <summary>後続断片の本文を誤ってチェックサムとして書き換えません。</summary>
    [Fact]
    public void PreservesNonInitialFragmentBytes()
    {
        byte[] ip = Capture.DemoCapture.Create(2).Data;
        ip[7] = 1;
        byte[] changed = ip.ToArray();
        changed[36] ^= 1;
        Assert.NotEqual(PacketFingerprint.Compute(ip, 101), PacketFingerprint.Compute(changed, 101));
    }

    /// <summary>非 IP はリンク種別と原本の両方を照合します。</summary>
    [Fact]
    public void IncludesLinkTypeForNonIp()
    {
        byte[] bytes = "not an IP packet"u8.ToArray();
        Assert.NotEqual(PacketFingerprint.Compute(bytes, 1), PacketFingerprint.Compute(bytes, 147));
        Assert.Equal(PacketFingerprint.Compute(bytes, 147), PacketFingerprint.Compute(bytes, 147));
        Assert.Matches("^[0-9a-f]{64}$", PacketFingerprint.Compute(bytes, 147));
    }
}
