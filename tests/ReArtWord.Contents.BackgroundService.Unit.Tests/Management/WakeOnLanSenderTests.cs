using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>電源操作を伴わず、隔離したUDP受信でMagic Packetの実送信を検証します。</summary>
public sealed class WakeOnLanSenderTests
{
    /// <summary>受信した実バイトが102バイトの正しいMagic Packetであることを確認します。</summary>
    /// <returns>ループバックUDPの受信完了です。</returns>
    [Fact]
    public async Task SendsExactPacketsFromTheBoundInterface()
    {
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        int index = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(value => value.Address.Equals(IPAddress.Loopback))).GetIPProperties().GetIPv4Properties()!.Index;
        WakeOnLanSender sender = new(() => [(IPAddress.Loopback, IPAddress.Loopback, index), (IPAddress.Loopback, IPAddress.Loopback, index)], port);
        Guid operation = Guid.NewGuid();
        HostWakeResult result = await sender.SendAsync(new(operation, "test-host", ["02-11-22-33-44-55", "02-AA-BB-CC-DD-EE"]));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(operation, result.OperationId);
        Assert.Equal(2, result.PacketsSent);
        Assert.Equal(1, result.InterfacesUsed);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        foreach (byte[] mac in new[] { Convert.FromHexString("021122334455"), Convert.FromHexString("02AABBCCDDEE") })
        {
            UdpReceiveResult packet = await receiver.ReceiveAsync(timeout.Token);
            Assert.Equal(IPAddress.Loopback, packet.RemoteEndPoint.Address);
            Assert.Equal(102, packet.Buffer.Length);
            Assert.All(packet.Buffer.Take(6), value => Assert.Equal(255, value));
            for (int repeat = 0; repeat < 16; repeat++)
            {
                Assert.Equal(mac, packet.Buffer.AsSpan(6 + repeat * 6, 6).ToArray());
            }
        }
    }

    /// <summary>別の口が失敗しても、送信済みを未送信に戻しません。</summary>
    /// <returns>一部送信と、送信前取消の確認です。</returns>
    [Fact]
    public async Task PartialFailureAndCancellationDoNotClaimStartup()
    {
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        WakeOnLanSender sender = new(() => [(IPAddress.Loopback, IPAddress.Loopback, 0), (IPAddress.Parse("192.0.2.250"), IPAddress.Loopback, 0)], port);
        HostWakeRequest request = new(Guid.NewGuid(), "test-host", ["02-11-22-33-44-55"]);
        HostWakeResult result = await sender.SendAsync(request);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(1, result.PacketsSent);
        Assert.Equal("wake.partial", result.Reason);
        HostWakeResult cancelled = await sender.SendAsync(request, new CancellationToken(true));
        Assert.Equal(EOutcome.NotExecuted, cancelled.Outcome);
        Assert.Equal(0, cancelled.PacketsSent);
        HostWakeResult noRoute = await new WakeOnLanSender(() => [], port).SendAsync(request);
        Assert.Equal(EOutcome.NotExecuted, noRoute.Outcome);
    }

    /// <summary>任意本文・マルチキャスト・ブロードキャストMACを受け付けません。</summary>
    [Theory]
    [InlineData("FF-FF-FF-FF-FF-FF")]
    [InlineData("01-00-5E-00-00-01")]
    [InlineData("00-00-00-00-00-00")]
    [InlineData("text")]
    [InlineData("02-11-22")]
    public void InvalidMacIsRejectedBeforeSending(string mac) => Assert.Throws<ArgumentException>(() => WakeOnLanSender.Packet(mac));
}
