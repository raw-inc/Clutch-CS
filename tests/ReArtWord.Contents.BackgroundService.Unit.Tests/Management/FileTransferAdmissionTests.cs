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

/// <summary>ファイル転送のTCP接続で、各操作の受付期限が送信先Coreの単調時計で判定されることを検証します。</summary>
public sealed class FileTransferAdmissionTests
{
    /// <summary>期限内の書込みは成功し、同じデータを期限後に再送しても書込みが実行されないことを確認します。</summary>
    /// <returns>時計差がある場合の受付と、期限切れ時の切断を検証するタスクです。</returns>
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

    /// <summary>単調時計の進み方が異なるPC間でも、期限内の次のファイル片を同じ接続で受け付けます。</summary>
    /// <returns>実TCP接続でファイル片の受付を検証するタスクです。</returns>
    [Fact]
    public async Task ClockRateDifferenceDoesNotDisconnectAnActiveTransfer()
    {
        AdmissionTestClock sourceClock = new(), targetClock = new();
        FileTransferPeer target = new("cluster", "peer", Guid.NewGuid());
        RequestAdmissionClock serverClock = new(target.InstanceId, targetClock);
        RequestClockClient senderClock = new(sourceClock);
        ManagementRegistry registry = new();
        registry.WriteCommand("transfer.write", (command, _) => Task.FromResult<object?>(command.Admission!.Remaining.Ticks));
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

        async Task ConfirmChunkAsync()
        {
            ManagementCommand command = new(Guid.NewGuid(), target.ClusterId, target.PeerId, target.InstanceId,
                sourceClock.GetUtcNow().AddSeconds(5), "transfer.write", NodeValue.FromObject(true));
            ClockedClusterRequest timed = (await senderClock.PrepareAsync(ManagementWire.Request(command, false), (_, _) =>
                Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(serverClock.Capture()))), timeout.Token).ConfigureAwait(true))!;
            await FileTransferWire.WriteAsync(stream, new ClockedFileCommand(command, new(timed.ClockInstanceId, timed.StartByTicks, timed.SourceNow)), timeout.Token).ConfigureAwait(true);
            ManagementReply reply = await FileTransferWire.ReadAsync<ManagementReply>(stream, timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, reply.Outcome);
            Assert.InRange(reply.Value!.Json.GetInt64(), 1, TimeSpan.FromSeconds(5).Ticks);
        }

        await ConfirmChunkAsync().ConfigureAwait(true);
        sourceClock.Advance(TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(1));
        targetClock.Advance(TimeSpan.FromSeconds(10));
        await ConfirmChunkAsync().ConfigureAwait(true);
    }
}
