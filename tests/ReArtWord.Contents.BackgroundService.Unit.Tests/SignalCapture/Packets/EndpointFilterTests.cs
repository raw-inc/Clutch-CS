using System.Buffers.Binary;
using System.Net;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Packets.Tests;

/// <summary>中継通信を送信元・宛先の両方向で除外します。</summary>
public sealed class EndpointFilterTests
{
    /// <summary>IP とポートの両方が一致した TCP を除外します。</summary>
    [Theory]
    [InlineData("127.0.0.1", 45000, true)]
    [InlineData("127.0.0.2", 80, true)]
    [InlineData("::ffff:127.0.0.2", 80, true)]
    [InlineData("127.0.0.2", 45000, false)]
    [InlineData("127.0.0.3", 80, false)]
    public void MatchesEitherTcpEndpoint(string address, int port, bool excluded)
    {
        var filter = new EndpointFilter(new[] { new EndpointRequest(address, port) });
        Assert.Equal(excluded, filter.Excludes(DemoCapture.Create(2).Data, 101));
        Assert.False(filter.Excludes(DemoCapture.Create(0).Data, 101));
    }

    /// <summary>オフロード・snaplen で末尾が欠けても、読める TCP endpoint は除外します。</summary>
    [Fact]
    public void ExcludesTruncatedTcpAndConservativeFragments()
    {
        var filter = new EndpointFilter(new[] { new EndpointRequest("127.0.0.2", 80) });
        byte[] tcp = DemoCapture.Create(2).Data;
        Assert.True(filter.Excludes(tcp.AsSpan(0, 40), 101));
        tcp[7] = 1;
        Assert.True(filter.Excludes(tcp, 101));
        Assert.False(filter.Excludes(tcp.AsSpan(0, 19), 101));
    }

    /// <summary>IPv6 拡張ヘッダー後のポートも照合します。</summary>
    [Fact]
    public void MatchesIpv6WithDestinationOptions()
    {
        var bytes = new byte[68];
        bytes[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 28);
        bytes[6] = 60;
        IPAddress.Parse("2001:db8::1").GetAddressBytes().CopyTo(bytes, 8);
        IPAddress.Parse("2001:db8::2").GetAddressBytes().CopyTo(bytes, 24);
        bytes[40] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(48), 1234);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(50), 443);
        var filter = new EndpointFilter(new[] { new EndpointRequest("2001:db8::2", 443) });
        Assert.True(filter.Excludes(bytes, 101));
    }

    /// <summary>名前解決や範囲外ポートを受け付けません。</summary>
    [Theory]
    [InlineData("localhost", 80)]
    [InlineData("127.0.0.1", -1)]
    [InlineData("127.0.0.1", 65536)]
    [InlineData("0.0.0.0", 80)]
    [InlineData("::", 80)]
    [InlineData("::ffff:0.0.0.0", 80)]
    public void RejectsInvalidEndpoint(string address, int port)
    {
        Assert.Throws<ArgumentException>(() => new EndpointFilter(new[] { new EndpointRequest(address, port) }));
    }

    /// <summary>UDPの除外は同一IPのTCPへ広がらず、両指定の合計件数を制限します。</summary>
    [Fact]
    public void SupportsUdpAndEnforcesCombinedLimit()
    {
        var filter = new EndpointFilter(null, new[] { new TransportEndpointRequest("udp", "::ffff:127.0.0.2", 9000) });
        Assert.True(filter.Excludes(DemoCapture.Create(0).Data, 101));
        Assert.False(filter.Excludes(DemoCapture.Create(1).Data, 101));
        Assert.False(filter.Excludes(DemoCapture.Create(2).Data, 101));
        EndpointRequest[] tcp = Enumerable.Repeat(new EndpointRequest("127.0.0.1", 80), 256).ToArray();
        Assert.Throws<ArgumentException>(() => new EndpointFilter(tcp, new[] { new TransportEndpointRequest("udp", "127.0.0.2", 9000) }));
        Assert.Throws<ArgumentException>(() => new EndpointFilter(null, new[] { new TransportEndpointRequest("quic", "127.0.0.2", 9000) }));
    }
}
