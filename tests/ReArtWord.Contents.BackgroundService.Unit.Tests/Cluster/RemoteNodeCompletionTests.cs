using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>通信待ち期限と開始済み処理の寿命を分けます。</summary>
public sealed class RemoteNodeCompletionTests
{
    /// <summary>準備状態が下がっても、同じ実体で開始済みの実処理は元の結果だけを照会します。</summary>
    /// <returns>新規実行を許可せず、未完了結果を実体喪失と誤判定しない確認です。</returns>
    [Fact]
    public async Task StartedExecutionCanCompleteWhileItsPeerIsTemporarilyUnavailableAsync()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        await using PeerCluster first = Peer(Repository(), id, "first", port, true);
        await using PeerCluster second = Peer(Repository(), id, "second", port, false);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        second.RemoteExecutionHandler = async (_, token) =>
        {
            Interlocked.Increment(ref count);
            started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NodeResult.Success(NodeValue.FromObject(42));
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await first.StartAsync(timeout.Token);
        await second.StartAsync(timeout.Token);
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token);
        }
        RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "fixture", "revision", new("node", "test", NodeSettings.Schema("{}")),
            new Dictionary<string, NodeValue>(), "second", second.LocalInstanceId, DateTimeOffset.UtcNow.AddSeconds(10));
        Task<NodeResult> execution = first.ExecuteAsync(request, timeout.Token);
        NodeResult result;
        try
        {
            await started.Task.WaitAsync(timeout.Token);
            second.ReadinessCheck = _ => ValueTask.FromResult(false);
            while (first.GetPeers().Single(peer => peer.InstanceId == second.LocalInstanceId).Status != EPeerStatus.Unavailable)
            {
                await Task.Delay(20, timeout.Token);
            }
            Assert.False(execution.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            result = await execution.ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, count);
    }

    /// <summary>長い遠隔実処理を一度だけ開始し、期限後は保存結果を照会します。</summary>
    /// <returns>実UDP検証完了。</returns>
    [Fact]
    public async Task LongRemoteExecutionCompletesThroughReadOnlyReceiptQuery()
    {
        int port = Port();
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository firstRepository = Repository();
        SqliteRepository secondRepository = Repository();
        await using PeerCluster first = Peer(firstRepository, id, "first", port, true);
        await first.StartAsync();
        await using PeerCluster second = Peer(secondRepository, id, "second", port, false);
        int executions = 0;
        TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        second.RemoteExecutionHandler = async (_, token) =>
        {
            if (Interlocked.Increment(ref executions) == 2)
            {
                secondStarted.TrySetResult();
            }
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            return NodeResult.Success(NodeValue.FromObject(7));
        };
        await second.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token);
        }
        second.SetLocalLogPort(34567);
        RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "demo", "revision", new NodeDefinition("node", "test", NodeSettings.Schema("{}")), new Dictionary<string, NodeValue>(StringComparer.Ordinal), "second", second.LocalInstanceId, DateTimeOffset.UtcNow.AddMilliseconds(200));
        NodeResult result = await first.ExecuteAsync(request, timeout.Token);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, executions);
        Assert.Equal(34567, first.GetPeers().Single(peer => peer.InstanceId == second.LocalInstanceId).LogPort);
        ClusterRequest command = new(request.Id, "node.execute", request.TargetPeerId, request.TargetInstanceId, request.ExpiresAt, NodeValue.FromObject(request));
        ClusterRequestStatus known = await first.QueryRequestAsync(command, timeout.Token);
        Assert.False(known.Running);
        Assert.Equal(EOutcome.Succeeded, known.Response!.Outcome);
        ClusterRequestStatus missing = await first.QueryRequestAsync(command with { Id = Guid.NewGuid() }, timeout.Token);
        Assert.Equal("Execution.ReceiptMissing", missing.Reason);
        Assert.Equal(1, executions);
        Assert.Single(await secondRepository.ListAsync("receipts:second"));
        RemoteExecutionRequest cancelled = request with { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        using CancellationTokenSource caller = new();
        Task<NodeResult> execution = first.ExecuteAsync(cancelled, caller.Token);
        await secondStarted.Task.WaitAsync(timeout.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await execution.ConfigureAwait(false));
        ClusterRequest cancelledCommand = new(cancelled.Id, "node.execute", cancelled.TargetPeerId, cancelled.TargetInstanceId, cancelled.ExpiresAt, NodeValue.FromObject(cancelled));
        ClusterRequestStatus completed = await first.QueryRequestAsync(cancelledCommand, timeout.Token);
        while (completed.Response is null)
        {
            // 呼出側の取消後も続く実処理と保存完了を観測し、共有ホストの負荷を固定sleepで推測しません。
            await Task.Delay(20, timeout.Token);
            completed = await first.QueryRequestAsync(cancelledCommand, timeout.Token);
        }
        Assert.Equal(EOutcome.Succeeded, completed.Response.Outcome);
        Assert.Equal(2, executions);
    }

    /// <summary>既存Started記録の照会は処理を起動しません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task MissingReceiptQueryDoesNotCreateStartedRecord()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster peer = Peer(repository, Guid.NewGuid().ToString("N"), "first", Port(), true);
        await peer.StartAsync();
        ClusterRequest request = new(Guid.NewGuid(), "test", "first", peer.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null);
        int executions = 0;
        peer.RegisterHandler("test", (command, token) =>
        {
            executions++;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        });
        peer.RegisterInspection("probe", (command, token) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(42))));
        Assert.Equal(EOutcome.NotExecuted, (await peer.InspectAsync(request)).Outcome);
        Assert.Equal(42, (await peer.InspectAsync(request with { Kind = "probe" })).Value!.Json.GetInt32());
        Assert.Equal(0, executions);
        Assert.Equal("Execution.ReceiptMissing", (await peer.QueryRequestAsync(request)).Reason);
        Assert.Empty(await repository.ListAsync("receipts:first"));
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("receipts:first", request.Id.ToString("N"), null, NodeValue.FromObject(new { Fingerprint = ContractJson.Hash(request), InstanceId = request.TargetInstanceId, request.ExpiresAt, Response = (ClusterResponse?)null }))]);
        ClusterRequestStatus interrupted = await peer.QueryRequestAsync(request);
        Assert.False(interrupted.Running);
        Assert.Null(interrupted.Response);
        Assert.Equal("Execution.StartedWithoutResult", interrupted.Reason);
    }

    /// <summary>短い通信待ちだけを設定します。</summary>
    /// <returns>試験Peer。</returns>
    private static PeerCluster Peer(IDurableRepository repository, string id, string name, int port, bool fresh) => new(new ClusterOptions(id, name, name, "127.0.0.1", "127.255.255.255", port, 1, fresh, Path.GetTempPath()) { RequestTimeout = TimeSpan.FromMilliseconds(300), HeartbeatInterval = TimeSpan.FromMilliseconds(50) }, repository) { ReadinessCheck = token => ValueTask.FromResult(true) };

    /// <summary>独立した保存先です。</summary>
    /// <returns>SQLite保存先。</returns>
    private static SqliteRepository Repository() => new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));

    /// <summary>試験用ポートを取得します。</summary>
    /// <returns>UDP port。</returns>
    private static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
