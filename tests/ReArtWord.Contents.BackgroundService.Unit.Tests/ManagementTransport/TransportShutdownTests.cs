using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>上位サービス解放前に通信コールバックの終了を待つ境界を確認します。</summary>
public sealed class TransportShutdownTests
{
    /// <summary>処理中コールバックを残して停止完了を返さず、所有側からの再停止も許します。</summary>
    /// <returns>実UDP受信と停止待合せの確認です。</returns>
    [Fact]
    public async Task StopWaitsForInFlightCallbackBeforeOwnerCanDispose()
    {
        using Socket reservation = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reservation.Bind(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        reservation.Close();
        string network = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        string cluster = Guid.NewGuid().ToString("N");
        Guid senderId = Guid.NewGuid();
        Guid receiverId = Guid.NewGuid();
        UdpTransport sender = new(new("sender", "127.0.0.1", "127.255.255.255", port, cluster), senderId, TimeProvider.System);
        UdpTransport receiver = new(new("receiver", "127.0.0.1", "127.255.255.255", port, cluster), receiverId, TimeProvider.System);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable senderLifetime = sender.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable receiverLifetime = receiver.ConfigureAwait(true);
        sender.Initialize(network);
        receiver.Initialize(network);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool completed = false;
        receiver.Received = async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            completed = true;
        };
        receiver.Start();
        WireMessage message = new(Guid.NewGuid(), cluster, "sender", senderId, "probe", "receiver", null, JsonSerializer.SerializeToElement(new { })) { TargetInstanceId = receiverId };
        Task stopping = Task.CompletedTask;
        try
        {
            await sender.SendAsync(message);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stopping = receiver.StopAsync();
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(completed);
        await receiver.StopAsync();
    }
}
