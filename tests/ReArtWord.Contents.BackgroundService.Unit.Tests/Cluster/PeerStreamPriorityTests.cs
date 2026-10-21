using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Streams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>実TCPの処理枠を埋めても、保存段階と元結果の照会を進められることを確認します。</summary>
[Collection("TimingSensitive")]
public sealed class PeerStreamPriorityTests
{
    /// <summary>二つの送信元が長い処理を16件開始しても、開始済み結果の照会を失いません。</summary>
    /// <returns>独立した照会・保存枠と、元要求一回だけの実行確認です。</returns>
    [Fact]
    public async Task SaturatedRequestsLeaveReceiptsAndReplicationAvailableAsync()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        FileTestPeer first = new(id, "first", port, true), second = new(id, "second", port, false), third = new(id, "third", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable thirdLifetime = third.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));
        TaskCompletionSource saturated = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int admitted = 0;
        first.Cluster.RegisterHandler("fixture.held", async (_, token) =>
        {
            if (Interlocked.Increment(ref admitted) == 16)
            {
                saturated.TrySetResult();
            }
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return new(EOutcome.Succeeded);
        });
        await first.Cluster.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.Cluster.StartAsync(timeout.Token).ConfigureAwait(true);
        await third.Cluster.StartAsync(timeout.Token).ConfigureAwait(true);
        while (new[] { first, second, third }.Any(peer => peer.Cluster.GetPeers().Count(member => member.Status == EPeerStatus.Ready) != 3))
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        ClusterRequest[] requests = Enumerable.Range(0, 16).Select(_ => new ClusterRequest(Guid.CreateVersion7(), "fixture.held", "first",
            first.Cluster.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null)).ToArray();
        Task<ClusterResponse>[] pending = requests.Select((request, index) => (index < 8 ? second
                                                                                         : third).Cluster.RequestAsync(request, timeout.Token)).ToArray();
        try
        {
            await saturated.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.True((await first.Cluster.QueryRequestAsync(requests[0], timeout.Token).ConfigureAwait(true)).Running);
            Assert.True((await second.Cluster.QueryRequestAsync(requests[0], timeout.Token).ConfigureAwait(true)).Running);
            Assert.Equal(EOutcome.Succeeded, (await third.Cluster.ReplicateAsync(Guid.CreateVersion7(),
                [new("fixture:priority", "value", null, NodeValue.FromObject(42))], timeout.Token).ConfigureAwait(true)).Outcome);
            Assert.Equal(42, (await first.Repository.ReadAsync("fixture:priority", "value", timeout.Token).ConfigureAwait(true))!.Value!.Json.GetInt32());
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(pending).ConfigureAwait(true);
        }
        ClusterRequestStatus completed;
        do
        {
            completed = await second.Cluster.QueryRequestAsync(requests[0], timeout.Token).ConfigureAwait(true);
        } while (completed.Running);
        Assert.Equal(EOutcome.Succeeded, completed.Response!.Outcome);
        Assert.Equal(16, admitted);
    }

    /// <summary>更新中の旧送信側と、区分を通知する新送信側を同じ受付で扱います。</summary>
    /// <returns>要求・参照・結果照会と、旧フレームの実応答です。</returns>
    [Theory]
    [InlineData(false, "request", 0)]
    [InlineData(false, "inspect", 0)]
    [InlineData(false, "receipt.query", 0)]
    [InlineData(true, "request", 0)]
    [InlineData(true, "inspect", 0)]
    [InlineData(true, "receipt.query", 0)]
    [InlineData(false, "request", 11000)]
    [InlineData(true, "request", 11000)]
    public async Task LegacyAndPrioritizedFramesUseTheSameReceiverAsync(bool prioritized, string kind, int delayMilliseconds)
    {
        FileTestPeer peer = new(Guid.NewGuid().ToString("N"), "peer", Port(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        peer.Cluster.RegisterHandler("fixture.echo", async (request, token) =>
        {
            await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);
            return new(EOutcome.Succeeded, request.Payload);
        });
        peer.Cluster.RegisterInspection("fixture.echo", (request, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, request.Payload)));
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        Guid instance = peer.Cluster.LocalInstanceId;
        ClusterResponse capability = await peer.Cluster.InspectAsync(new(Guid.NewGuid(), PeerStreamTicket.Capability, "peer", instance,
            DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null)).ConfigureAwait(true);
        PeerStreamTicket ticket = capability.Value!.Json.Deserialize<PeerStreamTicket>()! with { PrioritizedFrames = prioritized };
        string network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => address.Address is "127.0.0.1")).InterfaceId;
        PeerStreamClient client = new(peer.Cluster, () => new(network, "127.0.0.1"), (message, _) => Task.FromResult<WireMessage?>(
            WireMessage.Reply(message, "peer", instance, JsonSerializer.SerializeToElement(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(ticket))))),
            TimeSpan.FromSeconds(20), 1048576);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        ClusterRequest command = new(Guid.NewGuid(), "fixture.echo", "peer", instance, DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.FromObject(42));
        string query = "receipt.query";
        JsonElement body = string.Equals(kind, query, StringComparison.Ordinal) ? JsonSerializer.SerializeToElement(new RequestQuery(command.Id, "missing"))
                                                                                : JsonSerializer.SerializeToElement(command);
        WireMessage request = new(Guid.NewGuid(), peer.Cluster.LocalClusterId, "peer", instance, kind, "peer", null, body, instance);
        WireMessage? response = await client.ExchangeAsync(request).ConfigureAwait(true);
        Assert.NotNull(response);
        if (string.Equals(kind, query, StringComparison.Ordinal))
        {
            Assert.Equal("Execution.ReceiptMissing", response.Body.Deserialize<ClusterRequestStatus>()!.Reason);
        }
        else
        {
            Assert.Equal(42, response.Body.Deserialize<ClusterResponse>()!.Value!.Json.GetInt32());
        }
    }

    /// <summary>処理要求を結果照会の枠へ偽装しても、本文の実行前に接続を閉じます。</summary>
    /// <returns>区分と本文の不一致による副作用の不存在です。</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MismatchedFrameLaneCannotExecuteARequestAsync(int lane)
    {
        FileTestPeer peer = new(Guid.NewGuid().ToString("N"), "peer", Port(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        int effects = 0;
        peer.Cluster.RegisterHandler("fixture.effect", (_, _) =>
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        });
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        PeerInfo member = Assert.Single(peer.Cluster.GetPeers());
        ClusterResponse capability = await peer.Cluster.InspectAsync(new(Guid.NewGuid(), PeerStreamTicket.Capability, "peer", member.InstanceId,
            DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null)).ConfigureAwait(true);
        PeerStreamTicket ticket = capability.Value!.Json.Deserialize<PeerStreamTicket>()!;
        string network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => address.Address is "127.0.0.1")).InterfaceId;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using PeerStreamConnection connection = await PeerStreamConnection.OpenAsync(new(network, "127.0.0.1"), member, ticket, member.InstanceId,
            TimeSpan.FromSeconds(2), timeout.Token, timeout.Token).ConfigureAwait(true);
        ClusterRequest command = new(Guid.NewGuid(), "fixture.effect", "peer", member.InstanceId, DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null);
        WireMessage request = new(Guid.NewGuid(), peer.Cluster.LocalClusterId, "peer", member.InstanceId, "request", "peer", null,
            JsonSerializer.SerializeToElement(command), member.InstanceId);
        await PeerStreamFrame.WriteAsync(connection.Stream, JsonSerializer.SerializeToUtf8Bytes(request), lane, timeout.Token).ConfigureAwait(true);
        Exception? closed = await Record.ExceptionAsync(async () =>
            Assert.Null(await TcpFrame.ReadAsync(connection.Stream, "lengthPrefix", 1048576, timeout.Token).ConfigureAwait(true))).ConfigureAwait(true);
        if (closed is not null)
        {
            // 未受信の本文がある接続は、OSによってEOFまたはリセットとして観測されます。
            Assert.IsAssignableFrom<IOException>(closed);
        }
        Assert.Equal(0, effects);
    }

    /// <summary>製品クラスタとは異なる試験用ポートを確保します。</summary>
    /// <returns>独立したUDP待受ポートです。</returns>
    private static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
