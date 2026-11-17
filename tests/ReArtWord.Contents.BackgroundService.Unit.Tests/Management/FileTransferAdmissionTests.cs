using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Files;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>TCP接続後の各操作も、別PCの日時ではなく対象実体の受付時計で判定します。</summary>
public sealed class FileTransferAdmissionTests
{
    /// <summary>有効な一回の書込みを受け付け、期限後の同じフレームでは書込みを増やしません。</summary>
    /// <returns>時計差と期限終了時の接続回収を、実TCPで確認します。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task ClockedFramesExpireWithoutDependingOnPcDates(int offsetSeconds)
    {
        AdmissionTestClock sourceClock = new(), targetClock = new();
        targetClock.Adjust(TimeSpan.FromSeconds(offsetSeconds));
        FileTransferPeer target = new("cluster", "peer", Guid.NewGuid());
        RequestAdmissionClock serverClock = new(target.InstanceId, targetClock);
        RequestClockClient senderClock = new(sourceClock);
        int effects = 0;
        ManagementRegistry registry = new();
        registry.Write("transfer.write", (_, _) =>
        {
            effects++;
            return Task.FromResult<object?>(true);
        });
        string address = "127.0.0.1";
        ClientNetworkInterface network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(value => string.Equals(value.Address, address, StringComparison.Ordinal)));
        FileTransferSession session = new(new(network.InterfaceId, address), target, registry, serverClock);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = session.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, session.Ticket.Port, timeout.Token);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString(session.Ticket.Token), timeout.Token);
        byte[] ack = new byte[1];
        await stream.ReadExactlyAsync(ack, timeout.Token);
        Assert.Equal(1, ack[0]);
        ManagementCommand original = new(Guid.NewGuid(), target.ClusterId, target.PeerId, target.InstanceId, sourceClock.GetUtcNow().AddSeconds(5), "transfer.write", NodeValue.FromObject(true));
        ClockedClusterRequest prepared = (await senderClock.PrepareAsync(ManagementWire.Request(original, false), (_, _) =>
            Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(serverClock.Capture()))), timeout.Token))!;
        ClockedFileCommand frame = new(original, new(prepared.ClockInstanceId, prepared.StartByTicks, prepared.SourceNow));
        await FileTransferWire.WriteAsync(stream, frame, timeout.Token);
        Assert.Equal(EOutcome.Succeeded, (await FileTransferWire.ReadAsync<ManagementReply>(stream, timeout.Token)).Outcome);
        Assert.Equal(1, effects);
        sourceClock.Advance(TimeSpan.FromSeconds(5));
        targetClock.Advance(TimeSpan.FromSeconds(5));
        targetClock.Adjust(TimeSpan.FromHours(-2));
        await FileTransferWire.WriteAsync(stream, frame, timeout.Token);
        Assert.Equal(0, await stream.ReadAsync(ack, timeout.Token));
        await session.Completion.WaitAsync(timeout.Token);
        Assert.Equal(1, effects);
    }
}
