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

/// <summary>固定TCP受付の実配送と、送信前後で異なるフォールバック境界を検証します。</summary>
public sealed class PeerRequestStreamTests
{
    /// <summary>待受を閉じた後も、同じ確立済み接続で次の要求を送り、ポートを要求数だけ消費しません。</summary>
    /// <returns>二つの要求と応答の対応、および接続再利用です。</returns>
    // 抑制理由: TestTaskLifetimeで受付を取り消して終了を待ち、TcpListenerより先に解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "TestTaskLifetimeで受付タスクの終了を待ってから、TcpListenerを破棄します。")]
    [Fact]
    public async Task ConfirmedConnectionIsReusedAfterTheListenerClosesAsync()
    {
        FileTestPeer peer = await FileTestPeer.CreateAsync(Guid.NewGuid().ToString("N"), "peer", Port(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync();
        string network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => address.Address is "127.0.0.1")).InterfaceId;
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        PeerStreamTicket ticket = new(((IPEndPoint)server.LocalEndpoint).Port, Guid.NewGuid(), peer.Cluster.LocalInstanceId, 1048576);
        PeerStreamClient client = new(peer.Cluster, () => new(network, "127.0.0.1"), (message, _) =>
        {
            Assert.Equal("inspect", message.Kind);
            return Task.FromResult<WireMessage?>(WireMessage.Reply(message, "peer", peer.Cluster.LocalInstanceId,
                JsonSerializer.SerializeToElement(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(ticket)))));
        }, TimeSpan.FromSeconds(2), 1048576);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task serving = ServeReusedAsync(server, timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable servingLifetime = new TestTaskLifetime(serving, timeout).ConfigureAwait(true);
        for (int index = 0; index < 2; index++)
        {
            WireMessage request = new(Guid.NewGuid(), peer.Cluster.LocalClusterId, "peer", peer.Cluster.LocalInstanceId, "request", "peer", null,
                JsonSerializer.SerializeToElement(index), peer.Cluster.LocalInstanceId);
            WireMessage? response = await client.ExchangeAsync(request, timeout.Token).ConfigureAwait(true);
            Assert.NotNull(response);
            Assert.Equal(request.Id, response.ReplyTo);
            Assert.Equal(index, response.Body.GetInt32());
        }
        await serving.ConfigureAwait(true);
    }

    /// <summary>一回だけ接続を受け、同じストリーム上の二フレームを順に返します。</summary>
    /// <returns>確立済み接続だけの終了です。</returns>
    private static async Task ServeReusedAsync(TcpListener server, CancellationToken cancellationToken = default)
    {
        using TcpClient socket = await server.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        server.Stop();
        using NetworkStream stream = socket.GetStream();
        await stream.ReadExactlyAsync(new byte[56], cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < 2; index++)
        {
            byte[]? bytes = await TcpFrame.ReadAsync(stream, "lengthPrefix", 1048576, cancellationToken).ConfigureAwait(false);
            Assert.NotNull(bytes);
            WireMessage request = JsonSerializer.Deserialize<WireMessage>(bytes)!;
            WireMessage response = WireMessage.Reply(request, request.TargetId!, request.TargetInstanceId!.Value, request.Body);
            await TcpFrame.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response), "lengthPrefix", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>開始要求が混雑しても確認経路を空け、取消した送信待ちを新しい要求として配送しません。</summary>
    /// <returns>送信枠の上限、確認優先、取消後の再利用です。</returns>
    [Theory]
    [InlineData("request", 8)]
    [InlineData("inspect", 16)]
    public async Task QueuedRequestsLeaveCapacityForConfirmationsAndReleaseOnCancellationAsync(string blockedKind, int capacity)
    {
        FileTestPeer peer = await FileTestPeer.CreateAsync(Guid.NewGuid().ToString("N"), "peer", Port(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync();
        TaskCompletionSource saturated = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, admitted = 0;
        PeerStreamClient client = new(peer.Cluster, () => throw new InvalidOperationException("UDP fixture only."), async (message, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(message.Kind, blockedKind, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref admitted);
                if (Interlocked.Increment(ref active) == capacity)
                {
                    saturated.TrySetResult();
                }
                try
                {
                    await release.Task.WaitAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
            return WireMessage.Reply(message, "peer", peer.Cluster.LocalInstanceId, JsonSerializer.SerializeToElement(new ClusterResponse(EOutcome.Succeeded)));
        }, TimeSpan.FromSeconds(2), 1048576, enabled: false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        using CancellationTokenSource stop = new(), limit = new(TimeSpan.FromSeconds(10));
        WireMessage Request(string kind) => new(Guid.NewGuid(), peer.Cluster.LocalClusterId, "peer", peer.Cluster.LocalInstanceId,
            kind, "peer", null, JsonSerializer.SerializeToElement(42), peer.Cluster.LocalInstanceId);
        Task<WireMessage?>[] pending = Enumerable.Range(0, capacity * 3).Select(_ => client.ExchangeAsync(Request(blockedKind), stop.Token)).ToArray();
        try
        {
            await saturated.Task.WaitAsync(limit.Token).ConfigureAwait(true);
            Assert.Equal(capacity, Volatile.Read(ref admitted));
            Assert.NotNull(await client.ExchangeAsync(Request("receipt.query"), limit.Token).ConfigureAwait(true));
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(pending)).ConfigureAwait(true);
        }
        Assert.Equal(0, active);
        Assert.Equal(capacity, admitted);
        release.TrySetResult();
        Assert.NotNull(await client.ExchangeAsync(Request("request"), limit.Token).ConfigureAwait(true));
    }

    /// <summary>二つの実Peerでファイル相当の大きな要求を配送し、同IDの副作用を一度だけにします。</summary>
    /// <returns>TCPの利用、本文の一致、再実行の抑止です。</returns>
    [Fact]
    public async Task StreamRequestsUseTheSameDurableExecutionBoundaryAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = Port();
        FileTestPeer first = await FileTestPeer.CreateAsync(cluster, "first", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        FileTestPeer second = await FileTestPeer.CreateAsync(cluster, "second", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        int writes = 0;
        second.Cluster.RegisterHandler("fixture.write", async (request, token) =>
        {
            Interlocked.Increment(ref writes);
            await File.WriteAllBytesAsync(Path.Combine(second.Root, "received.bin"), request.Payload.Binary.ToArray(), token).ConfigureAwait(false);
            return new(EOutcome.Succeeded, NodeValue.FromObject(request.Payload.Binary.Length));
        });
        await first.Cluster.StartAsync();
        await second.Cluster.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (first.Cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2 || second.Cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token);
        }
        byte[] bytes = new byte[131072];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        ClusterRequest request = new(Guid.NewGuid(), "fixture.write", "second", second.Cluster.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.FromBytes(bytes));
        long before = second.Cluster.StreamRequestsReceived;
        Assert.Equal(EOutcome.Succeeded, (await first.Cluster.RequestAsync(request, timeout.Token)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await first.Cluster.RequestAsync(request, timeout.Token)).Outcome);
        Assert.Equal(1, writes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(second.Root, "received.bin"), timeout.Token));
        Assert.True(before + 2 <= second.Cluster.StreamRequestsReceived);
    }

    /// <summary>本文を送った後の切断は、UDP再送で副作用を増やしません。</summary>
    /// <returns>旧版・接続拒否ではフォールバックし、受付後の欠測では確認不能を返します。</returns>
    // 抑制理由: TestTaskLifetimeで受付を取り消して終了を待ち、TcpListenerより先に解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "TestTaskLifetimeで受付タスクの終了を待ってから、TcpListenerを破棄します。")]
    [Theory]
    [InlineData("legacy", 1, 0)]
    [InlineData("before", 1, 0)]
    [InlineData("after", 0, 1)]
    [InlineData("success", 0, 1)]
    [InlineData("wrong-reply", 0, 1)]
    public async Task FallbackNeverReplaysAnAdmittedRequestAsync(string mode, int expectedFallback, int expectedBodies)
    {
        const string legacy = "legacy", inspection = "inspect";
        FileTestPeer peer = await FileTestPeer.CreateAsync(Guid.NewGuid().ToString("N"), "peer", Port(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        await peer.Cluster.StartAsync();
        PeerInfo member = Assert.Single(peer.Cluster.GetPeers());
        string network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => address.Address is "127.0.0.1")).InterfaceId;
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        PeerStreamTicket ticket = new(((IPEndPoint)server.LocalEndpoint).Port, Guid.NewGuid(), member.InstanceId, 1048576);
        int fallback = 0, bodies = 0;
        PeerStreamClient client = new(peer.Cluster, () => new(network, "127.0.0.1"), (message, token) =>
        {
            token.ThrowIfCancellationRequested();
            JsonElement response;
            if (message.Kind is inspection)
            {
                response = JsonSerializer.SerializeToElement(mode is legacy ? new ClusterResponse(EOutcome.NotExecuted, Reason: "Cluster.InspectionTargetUnavailable")
                                                                            : new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(ticket)));
            }
            else
            {
                Interlocked.Increment(ref fallback);
                response = JsonSerializer.SerializeToElement(new ClusterResponse(EOutcome.Succeeded));
            }
            return Task.FromResult<WireMessage?>(WireMessage.Reply(message, member.PeerId, member.InstanceId, response));
        }, TimeSpan.FromSeconds(2), 1048576);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task serving = mode is legacy ? Task.CompletedTask
                                      : ServeAsync(server, mode, () => Interlocked.Increment(ref bodies), timeout.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable servingLifetime = new TestTaskLifetime(serving, timeout).ConfigureAwait(true);
        WireMessage request = new(Guid.NewGuid(), peer.Cluster.LocalClusterId, member.PeerId, member.InstanceId, "request", member.PeerId, null,
            JsonSerializer.SerializeToElement(new { id = Guid.NewGuid(), value = "one write" }), member.InstanceId);
        WireMessage? reply = await client.ExchangeAsync(request, timeout.Token);
        await serving.ConfigureAwait(true);
        Assert.Equal(expectedFallback, fallback);
        Assert.Equal(expectedBodies, bodies);
        Assert.Equal(mode is "after" or "wrong-reply", reply is null);
    }

    /// <summary>ヘッダーの前後に独立した切断点を置いた実TCP受付です。</summary>
    /// <returns>一接続の終了です。</returns>
    private static async Task ServeAsync(TcpListener server, string mode, Action accepted, CancellationToken cancellationToken = default)
    {
        const string before = "before", after = "after", wrong = "wrong-reply";
        using TcpClient socket = await server.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = socket.GetStream();
        byte[] header = new byte[56];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (mode is before)
        {
            return;
        }
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
        byte[] bytes = (await TcpFrame.ReadAsync(stream, "lengthPrefix", 1048576, cancellationToken).ConfigureAwait(false))!;
        WireMessage request = JsonSerializer.Deserialize<WireMessage>(bytes)!;
        accepted();
        if (mode is after)
        {
            return;
        }
        WireMessage reply = WireMessage.Reply(request, request.TargetId!, request.TargetInstanceId!.Value, JsonSerializer.SerializeToElement(new ClusterResponse(EOutcome.Succeeded)));
        if (mode is wrong)
        {
            reply = reply with { ReplyTo = Guid.NewGuid() };
        }
        await TcpFrame.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(reply), "lengthPrefix", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>試験用のUDPポートを確保します。</summary>
    /// <returns>利用できるローカルポートです。</returns>
    private static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
