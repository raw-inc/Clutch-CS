using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Infrastructure.Networking;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Networking;

/// <summary>実際のループバックTCPとUDPの端点・所有PIDを照合します。</summary>
public sealed class WindowsSocketOwnerTests
{
    /// <summary>TCPとUDPのプロトコル、実際の端点と所有者を照合します。</summary>
    /// <returns>Windowsが報告する実ソケットの確認です。</returns>
    [WindowsFact]
    public async Task FindsOwnedTcpAndUdpWithoutAdministratorCommandsAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(true);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
        using UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        IReadOnlyList<(SocketType Type, IPEndPoint Local, IPEndPoint? Remote, int ProcessId)> rows = WindowsSocketOwners.Read();
        Assert.Contains(rows, row => row.ProcessId == Environment.ProcessId && row.Type == SocketType.Stream && row.Local.Port == ((IPEndPoint)client.Client.LocalEndPoint!).Port && row.Remote?.Port == port && row.Local.Address.Equals(IPAddress.Loopback));
        Assert.Contains(rows, row => row.ProcessId == Environment.ProcessId && row.Type == SocketType.Dgram && row.Local.Port == udpPort && row.Local.Address.Equals(IPAddress.Loopback));
        if (Socket.OSSupportsIPv6)
        {
            using UdpClient ipv6 = new(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            int ipv6Port = ((IPEndPoint)ipv6.Client.LocalEndPoint!).Port;
            Assert.Contains(WindowsSocketOwners.Read(), row => row.ProcessId == Environment.ProcessId && row.Type == SocketType.Dgram && row.Local.Port == ipv6Port && row.Local.Address.Equals(IPAddress.IPv6Loopback));
        }
    }
}
