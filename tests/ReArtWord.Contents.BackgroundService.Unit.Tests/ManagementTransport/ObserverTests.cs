using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;

/// <summary>監視処理をCore参加へ混ぜず実UDP上で管理できることを確認します。</summary>
public sealed class ObserverTests
{
    /// <summary>再起動後は秘密ペイロードなしの指紋照会で元の完了結果だけを取得します。</summary>
    /// <returns>別実体で副作用を再実行しない検証完了。</returns>
    [Fact]
    public async Task RestartedPeerReturnsOriginalReceiptButDoesNotExecuteForOldInstance()
    {
        int port = Port();
        ClusterOptions options = Options(Guid.NewGuid().ToString("N"), "one", port, true);
        SqliteRepository repository = Repository();
        await using UdpObserver observer = Observer(port, TimeSpan.FromMilliseconds(250));
        await observer.StartAsync();
        ManagementCommand original;
        await using (PeerCluster first = Peer(options, repository, EchoAsync))
        {
            await first.StartAsync();
            await WaitAsync(() => observer.GetClusters().Count != 0);
            original = Command(first, "write", NodeValue.FromObject("original-sensitive-input"));
            ManagementReply accepted = await observer.ExecuteAsync(original);
            if (accepted.Outcome == EOutcome.Unknown)
            {
                // 応答窓と副作用完了は別なので、再送せず元IDの操作記録だけを確認します。
                using CancellationTokenSource confirmation = new(TimeSpan.FromSeconds(10));
                ManagementQueryResult observed = new(true);
                while (observed.Reply is null)
                {
                    observed = await observer.QueryAsync(original, confirmation.Token);
                    if (observed.Reply is null)
                    {
                        await Task.Delay(20, confirmation.Token);
                    }
                }
                accepted = observed.Reply;
            }
            Assert.Equal(EOutcome.Succeeded, accepted.Outcome);
        }
        int effects = 0;
        Task<ManagementReply> Handler(ManagementCommand command, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ManagementReply(EOutcome.Succeeded));
        }
        await using PeerCluster restarted = Peer(options, repository, Handler);
        await restarted.StartAsync();
        await WaitAsync(() => observer.GetClusters().SelectMany(item => item.Peers).Any(peer => peer.InstanceId == restarted.LocalInstanceId));
        ManagementReceiptQuery query = new(original.RequestId, original.ClusterId, original.TargetPeerId, ContractJson.Hash(ManagementWire.Request(original, false)));
        ManagementQueryResult result = await observer.QueryAsync(query);
        Assert.Equal(EOutcome.Succeeded, result.Reply!.Outcome);
        Assert.Equal("original-sensitive-input", result.Reply.Value!.GetString());
        Assert.NotEqual(EOutcome.Succeeded, (await observer.ExecuteAsync(original)).Outcome);
        Assert.Equal(0, effects);
    }

    /// <summary>開始操作記録だけを残した旧実体はUnknownであり、照会から実行しません。</summary>
    /// <returns>started-crashの検証完了。</returns>
    [Fact]
    public async Task StartedCrashAndMissingReceiptsDoNotStartCommands()
    {
        int port = Port();
        ClusterOptions options = Options(Guid.NewGuid().ToString("N"), "one", port, true);
        SqliteRepository repository = Repository();
        ManagementCommand original = new(Guid.NewGuid(), options.Id, options.PeerId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20), "write", NodeValue.FromObject("secret"));
        string fingerprint = ContractJson.Hash(ManagementWire.Request(original, false));
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("receipts:one", original.RequestId.ToString("N"), null,
            NodeValue.FromObject(new { Fingerprint = fingerprint, InstanceId = original.TargetInstanceId, original.ExpiresAt, Response = (ClusterResponse?)null }))]);
        int effects = 0;
        Task<ManagementReply> Handler(ManagementCommand command, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ManagementReply(EOutcome.Succeeded));
        }
        await using PeerCluster peer = Peer(options, repository, Handler);
        await peer.StartAsync();
        await using UdpObserver observer = Observer(port);
        await observer.StartAsync();
        await WaitAsync(() => observer.GetClusters().Count != 0);
        ManagementReceiptQuery query = new(original.RequestId, original.ClusterId, original.TargetPeerId, fingerprint);
        ManagementQueryResult started = await observer.QueryAsync(query);
        Assert.False(started.Running);
        Assert.Null(started.Reply);
        Assert.Equal("Execution.StartedWithoutResult", started.Reason);
        Assert.Null((await observer.QueryAsync(query with { RequestId = Guid.NewGuid() })).Reply);
        Assert.Equal(EOutcome.NotExecuted, (await observer.ExecuteAsync(original)).Outcome);
        Assert.Equal(0, effects);
    }

    /// <summary>複数クラスタを発見してもPeer数を増やさず、読取定期照会に操作記録を残しません。</summary>
    /// <returns>実発見・読取・重複管理要求の検証完了。</returns>
    [Fact]
    public async Task DiscoversMultipleClustersWithoutJoiningAndReadOnlyDoesNotAccumulateReceipts()
    {
        int port = Port();
        string firstId = Guid.NewGuid().ToString("N");
        string secondId = Guid.NewGuid().ToString("N");
        SqliteRepository firstStore = Repository();
        SqliteRepository secondStore = Repository();
        int effects = 0;
        ConcurrentDictionary<Guid, byte> notifications = new();
        Task<ManagementReply> Handler(ManagementCommand command, CancellationToken token)
        {
            string write = "write";
            if (string.Equals(command.Operation, write, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref effects);
            }
            string presence = "presence";
            if (string.Equals(command.Operation, presence, StringComparison.Ordinal))
            {
                _ = notifications.TryAdd(command.RequestId, 0);
            }
            return Task.FromResult(new ManagementReply(EOutcome.Succeeded, command.Payload));
        }
        await using PeerCluster first = Peer(Options(firstId, "one", port, true), firstStore, Handler);
        await using PeerCluster second = Peer(Options(firstId, "two", port, false), secondStore, Handler);
        await using PeerCluster other = Peer(Options(secondId, "other", port, true), Repository(), Handler);
        await first.StartAsync();
        await second.StartAsync();
        await other.StartAsync();
        await using UdpObserver observer = Observer(port);
        await observer.StartAsync();
        await WaitAsync(() => observer.GetClusters().Count == 2 && first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        Assert.Equal(2, first.GetPeers().Count);
        Assert.Equal(2, second.GetPeers().Count);
        Assert.Single(other.GetPeers());
        ManagementCommand read = Command(first, "status", NodeValue.FromObject(new { value = 1 }));
        int before = (await firstStore.ListAsync("receipts:one")).Count;
        for (int index = 0; index < 20; index++)
        {
            Assert.Equal(EOutcome.Succeeded, (await observer.InspectAsync(read with { RequestId = Guid.NewGuid() })).Outcome);
        }
        Assert.Equal(before, (await firstStore.ListAsync("receipts:one")).Count);
        ManagementCommand[] notices = Enumerable.Range(0, 20).Select(_ => read with { RequestId = Guid.NewGuid(), Operation = "presence" }).ToArray();
        foreach (ManagementCommand notice in notices)
        {
            Assert.Equal(EOutcome.Succeeded, (await observer.NotifyAsync(notice)).Outcome);
        }

        // 通知はUDP再送で重複するため、要求ごとの到着と操作記録の非永続性を確認します。
        Assert.Equal(EOutcome.Succeeded, (await observer.NotifyAsync(notices[0])).Outcome);
        Assert.Equal(notices.Length, notifications.Count);
        Assert.All(notices, notice => Assert.True(notifications.ContainsKey(notice.RequestId)));
        Assert.Equal(before, (await firstStore.ListAsync("receipts:one")).Count);
        Assert.Equal(EOutcome.NotExecuted, (await observer.NotifyAsync(read with { Operation = "write" })).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await observer.InspectAsync(read with { Operation = "presence" })).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await observer.InspectAsync(read with { Operation = "write" })).Outcome);
        Assert.Equal(0, effects);
        ManagementCommand writeCommand = Command(first, "write", NodeValue.FromObject(new string('x', 50000)));
        Assert.Equal(EOutcome.Succeeded, (await observer.ExecuteAsync(writeCommand)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await observer.ExecuteAsync(writeCommand)).Outcome);
        Assert.Equal(1, effects);
        Assert.Equal(EOutcome.Failed, (await observer.ExecuteAsync(writeCommand with { Payload = NodeValue.FromObject("different") })).Outcome);
        Assert.Equal(1, effects);
        ManagementQueryResult queried = await observer.QueryAsync(writeCommand);
        Assert.Equal(EOutcome.Succeeded, queried.Reply!.Outcome);
        Assert.Equal(writeCommand.Payload!.GetString(), queried.Reply.Value!.GetString());
    }

    /// <summary>応答期限と元処理の終了を区別し、照会だけで完了を取得します。</summary>
    /// <returns>Unknownと元処理継続の検証完了。</returns>
    [Fact]
    public async Task UnknownExecutionContinuesAndReadOnlyQueryReturnsOriginalResult()
    {
        int port = Port();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int effects = 0;
        async Task<ManagementReply> Handler(ManagementCommand command, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return new(EOutcome.Succeeded, NodeValue.FromObject("original"));
        }
        await using PeerCluster peer = Peer(Options(Guid.NewGuid().ToString("N"), "one", port, true), Repository(), Handler);
        await peer.StartAsync();
        await using UdpObserver observer = Observer(port, TimeSpan.FromMilliseconds(150));
        await observer.StartAsync();
        await WaitAsync(() => observer.GetClusters().Count != 0);
        ManagementCommand command = Command(peer, "write", NodeValue.Null);
        try
        {
            Task<ManagementReply> pending = observer.ExecuteAsync(command);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(EOutcome.Unknown, (await pending.ConfigureAwait(true)).Outcome);
            Assert.True((await observer.QueryAsync(command)).Running);
        }
        finally
        {
            release.TrySetResult();
        }
        ManagementQueryResult result = new(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (result.Reply is null)
        {
            result = await observer.QueryAsync(command, timeout.Token);
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal("original", result.Reply.Value!.GetString());
        Assert.Equal(1, effects);
    }

    /// <summary>ファイル確定が応答期限より長くても、書込みを再発行せず元結果だけを確認します。</summary>
    /// <returns>一度だけの副作用と確定応答です。</returns>
    [Fact]
    public async Task FileCommitWaitsForTheOriginalReceiptAfterTransportTimeout()
    {
        int port = Port(), effects = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ManagementReply> Handler(ManagementCommand command, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return new(EOutcome.Succeeded, NodeValue.FromObject(new Contracts.Files.Transfers.FileStageCommitResult("destination", "hash", 123)));
        }
        await using PeerCluster peer = Peer(Options(Guid.NewGuid().ToString("N"), "one", port, true), Repository(), Handler);
        await peer.StartAsync();
        await using UdpObserver observer = Observer(port, TimeSpan.FromMilliseconds(150));
        await observer.StartAsync();
        await WaitAsync(() => observer.GetClusters().Count != 0);
        PeerManagementClient client = new((_, _) => observer);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
        Task<Contracts.Files.Transfers.FileStageCommitResult> pending = client.SendAsync<Contracts.Files.Transfers.FileStageCommitResult>(
            new(peer.LocalClusterId, peer.LocalPeerId, peer.LocalInstanceId), "transfer.commit", new Contracts.Files.Transfers.FileStageToken(Guid.NewGuid()), false, timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            await Task.Delay(350, timeout.Token);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(123, (await pending.ConfigureAwait(true)).Bytes);
        Assert.Equal(1, effects);
    }

    /// <summary>未登録の照会をmode mismatchで拒否する旧Coreにも従来転送を維持します。</summary>
    /// <returns>未知操作の応答を使った互換性確認です。</returns>
    [Fact]
    public async Task FileTransferUsesLegacyChunksWhenAnOlderCoreHasNoStreamOperation()
    {
        int port = Port();
        await using PeerCluster peer = Peer(Options(Guid.NewGuid().ToString("N"), "one", port, true), Repository(), EchoAsync);
        await peer.StartAsync();
        await using UdpObserver observer = Observer(port);
        await observer.StartAsync();
        await WaitAsync(() => observer.GetClusters().Count != 0);
        PeerManagementClient client = new((_, _) => observer);
        using ReArtWord.Contents.BackgroundService.ManagementTransport.Files.FileTransferClient transfer = await client.OpenFileTransferAsync(new(peer.LocalClusterId, peer.LocalPeerId, peer.LocalInstanceId));
        Assert.Equal(32768, transfer.ChunkBytes);
    }

    /// <summary>指定NICが成立しない時に同IPの別NICへfallbackしません。</summary>
    /// <returns>明示binding条件の検証完了。</returns>
    [Fact]
    public async Task UnresolvedInterfaceCannotDiscoverOrExecute()
    {
        int port = Port();
        await using PeerCluster peer = Peer(Options(Guid.NewGuid().ToString("N"), "one", port, true), Repository(), EchoAsync);
        await peer.StartAsync();
        UdpObserver observer = new(new ObserverOptions("invalid", new ClientNetworkBinding("missing-interface", "127.0.0.1", "127.255.255.255", port)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = observer.ConfigureAwait(false);
        await observer.StartAsync();
        await Task.Delay(150);
        Assert.False(observer.GetNetworkState().Listening);
        Assert.Empty(observer.GetClusters());
        Assert.Equal(EOutcome.NotExecuted, (await observer.ExecuteAsync(Command(peer, "write", NodeValue.Null))).Outcome);
        Assert.Single(peer.GetPeers());
    }

    /// <summary>管理要求へ元の実体と有限受理期限を固定します。</summary>
    /// <returns>同じまま再送・照会する要求です。</returns>
    private static ManagementCommand Command(PeerCluster peer, string operation, NodeValue payload) => new(Guid.NewGuid(), peer.LocalClusterId, peer.LocalPeerId, peer.LocalInstanceId, DateTimeOffset.UtcNow.AddSeconds(20), operation, payload);

    /// <summary>一つの管理handlerを持つCoreを作ります。</summary>
    /// <returns>通常参加条件付きPeerです。</returns>
    private static PeerCluster Peer(ClusterOptions options, IDurableRepository repository, Func<ManagementCommand, CancellationToken, Task<ManagementReply>> handler)
    {
        PeerCluster peer = new(options, repository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        string status = "status";
        string presence = "presence";
        _ = new PeerManagementEndpoint(peer, handler, operation => string.Equals(operation, status, StringComparison.Ordinal), operation => string.Equals(operation, presence, StringComparison.Ordinal));
        return peer;
    }

    /// <summary>ループバックの明示NICを選択します。</summary>
    /// <returns>参加しないクライアントです。</returns>
    private static UdpObserver Observer(int port, TimeSpan? timeout = null)
    {
        string address = "127.0.0.1";
        ClientNetworkInterface network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(itemAddress => string.Equals(itemAddress.Address, address, StringComparison.Ordinal)));
        return new(new ObserverOptions(Guid.NewGuid().ToString("N"), new(network.InterfaceId, address, "127.255.255.255", port)) { RequestTimeout = timeout ?? TimeSpan.FromSeconds(2) });
    }

    /// <summary>独立保存先を作ります。</summary>
    /// <returns>SQLite保存先です。</returns>
    private static SqliteRepository Repository() => new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));

    /// <summary>Core用通信設定を作ります。</summary>
    /// <returns>一つのCluster/Peerの設定です。</returns>
    private static ClusterOptions Options(string cluster, string peer, int port, bool initialize) => new(cluster, peer, "host", "127.0.0.1", "127.255.255.255", port, 1, initialize, Path.GetTempPath())
    { HeartbeatInterval = TimeSpan.FromMilliseconds(50), PeerTimeout = TimeSpan.FromSeconds(2), RequestTimeout = TimeSpan.FromSeconds(2) };

    /// <summary>明示した入力をそのまま返します。</summary>
    /// <returns>検証用管理結果です。</returns>
    private static Task<ManagementReply> EchoAsync(ManagementCommand command, CancellationToken cancellationToken = default) => Task.FromResult(new ManagementReply(EOutcome.Succeeded, command.Payload));

    /// <summary>独立UDPポートを選びます。</summary>
    /// <returns>試験用ポートです。</returns>
    private static int Port()
    {
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>実発見・同期を有限時間だけ待ちます。</summary>
    /// <returns>条件成立です。</returns>
    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
    }
}
