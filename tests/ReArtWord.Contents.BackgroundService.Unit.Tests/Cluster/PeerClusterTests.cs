using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>実UDPで発見・同期・操作記録を検証します。</summary>
public sealed class PeerClusterTests
{
    /// <summary>複製済み実値を新Peerが取得し、同じ要求は一回だけ実行します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task BroadcastDiscoverySynchronizesDataAndDeduplicatesRequests()
    {
        int port = GetPort();
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), id);
        SqliteRepository firstStore = new(Path.Combine(path, "first.db"));
        SqliteRepository secondStore = new(Path.Combine(path, "second.db"));
        await using PeerCluster first = Create(id, "first", port, true, firstStore);
        await first.StartAsync();
        Assert.Equal(EOutcome.Succeeded, (await first.ReplicateAsync(Guid.NewGuid(), [new("configuration", "test", null, NodeValue.FromObject(42))])).Outcome);
        await using PeerCluster second = Create(id, "second", port, false, secondStore);
        NodeStateStore firstState = new(firstStore, "first");
        NodeStateStore secondState = new(secondStore, "second");
        using StateReplication firstStateReplication = new(first, firstStore, firstState);
        using StateReplication secondStateReplication = new(second, secondStore, secondState);
        Assert.NotNull(firstStateReplication);
        Assert.NotNull(secondStateReplication);
        int executions = 0;
        second.RegisterHandler("test", (request, token) =>
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, request.Payload));
        });
        await second.StartAsync();
        await WaitAsync(() => first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2 && second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        Assert.Equal(42, (await secondStore.ReadAsync("configuration", "test"))!.Value!.Json.GetInt32());
        TaskCompletionSource<int> transient = new(TaskCreationOptions.RunContinuationsAsynchronously);
        second.RegisterTransientHandler("draft", (sender, instance, value, token) =>
        {
            Assert.Equal("first", sender);
            Assert.Equal(first.LocalInstanceId, instance);
            transient.TrySetResult(value.Json.GetInt32());
            return Task.CompletedTask;
        });
        await first.PublishTransientAsync("draft", NodeValue.FromObject(7));
        Assert.Equal(7, await transient.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await secondStore.ListAsync("receipts:second"));
        DurableEntry previous = (await firstStore.ReadAsync("configuration", "test"))!;
        Guid savedId = Guid.NewGuid();
        Assert.Equal(EOutcome.Succeeded, (await first.ReplicateAsync(savedId, [new("configuration", "test", previous.Version, NodeValue.FromObject(43))])).Outcome);
        Assert.Equal(43, (await secondStore.ReadAsync("configuration", "test"))!.Value!.Json.GetInt32());
        Assert.True((await secondState.CompareExchangeAsync("workflow:demo", "counter", null, NodeValue.FromObject(1))).Applied);
        Assert.Equal(1, (await firstState.ReadAsync("workflow:demo", "counter"))!.Value.Json.GetInt32());
        Assert.Equal(EOutcome.Failed, (await firstState.CompareExchangeAsync("workflow:demo", "counter", null, NodeValue.FromObject(2))).Outcome);
        ClusterRequest request = new(Guid.NewGuid(), "test", "second", second.LocalInstanceId, DateTimeOffset.UtcNow.AddSeconds(20), NodeValue.FromObject(new string('x', 50000)));
        Assert.Equal(EOutcome.Succeeded, (await first.RequestAsync(request)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await first.RequestAsync(request)).Outcome);
        Assert.Equal(1, executions);
        Assert.Equal("Execution.RequestMismatch", (await first.RequestAsync(request with { Payload = NodeValue.FromObject("changed") })).Reason);
        Assert.Equal(1, executions);
        WorkflowCompiler compiler = new(new NodeRegistry([]));
        WorkflowStore firstWorkflows = new(firstStore, first, compiler)
        {
            ActivateHandler = (workflow, token) => Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded)),
        };
        CompiledWorkflow? secondEffective = null;
        WorkflowStore secondWorkflows = new(secondStore, second, compiler)
        {
            GetEffective = workflowId => secondEffective,
            ActivateHandler = (workflow, token) =>
            {
                secondEffective = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
        WorkflowSaveResult workflowSave = await firstWorkflows.SaveAsync(new WorkflowSaveRequest(Guid.NewGuid(), new WorkflowDefinition("demo", "saved", [], []), null));
        Assert.Equal(EOutcome.Succeeded, workflowSave.Activation.Outcome);
        Assert.NotNull(await secondWorkflows.GetAsync("demo"));
        Assert.Null(secondEffective);
        await secondWorkflows.ReconcileActivationAsync();
        Assert.Equal(workflowSave.Revision, secondEffective!.Revision);
    }

    /// <summary>開始期限を越えても開始済み処理は依頼元の取消から独立します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task StartedOperationCompletesAfterCallerStopsWaiting()
    {
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository store = new(Path.Combine(Path.GetTempPath(), id, "state.db"));
        RequestTestClock clock = new();
        await using PeerCluster peer = Create(id, "one", GetPort(), true, store, clock);
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RegisterHandler("long", async (request, token) =>
        {
            clock.AdvanceAndResume(TimeSpan.FromMilliseconds(100));
            await cancellation.CancelAsync().ConfigureAwait(false);
            Assert.False(token.IsCancellationRequested);
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return new ClusterResponse(EOutcome.Succeeded, request.Payload);
        });
        await peer.StartAsync();
        clock.Freeze();
        ClusterRequest request = new(Guid.NewGuid(), "long", "one", peer.LocalInstanceId, clock.GetUtcNow().AddMilliseconds(50), NodeValue.FromObject(5));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.RequestAsync(request, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(request.ExpiresAt < clock.GetUtcNow());
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(EOutcome.Succeeded, (await peer.RequestAsync(request, timeout.Token)).Outcome);
    }

    /// <summary>前回起動で開始だけ保存された要求は実行せず結果不明を返します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task StartedBeforeCrashIsNeverReexecuted()
    {
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository store = new(Path.Combine(Path.GetTempPath(), id, "state.db"));
        ClusterRequest request = new(Guid.NewGuid(), "test", "one", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.FromObject(5));
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("receipts:one", request.Id.ToString("N"), null, NodeValue.FromObject(new { Fingerprint = ContractJson.Hash(request), InstanceId = request.TargetInstanceId, request.ExpiresAt, Response = (ClusterResponse?)null }))]);
        await using PeerCluster peer = Create(id, "one", GetPort(), true, store);
        int executions = 0;
        peer.RegisterHandler("test", (input, token) =>
        {
            executions++;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        });
        await peer.StartAsync();
        Assert.Equal("Execution.StartedWithoutResult", (await peer.RequestAsync(request)).Reason);
        Assert.Equal(0, executions);
    }

    /// <summary>明示離脱中は許可されたローカル要求だけを実行できます。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task DetachedAllowsExplicitLocalOperationsAndSurvivesRestart()
    {
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository store = new(Path.Combine(Path.GetTempPath(), id, "state.db"));
        int port = GetPort();
        await using (PeerCluster peer = Create(id, "one", port, true, store))
        {
            peer.LocalDetachedExecutionAllowed = request => string.Equals(request.Kind, "local", StringComparison.Ordinal);
            peer.RegisterHandler("local", (input, token) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded)));
            await peer.StartAsync();
            await peer.DetachAsync();
            Assert.Equal(EOutcome.Succeeded, (await peer.RequestAsync(new ClusterRequest(Guid.NewGuid(), "local", "one", peer.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null))).Outcome);
        }
        await using PeerCluster restarted = Create(id, "one", port, false, store);
        await restarted.StartAsync();
        Assert.Equal(EPeerStatus.Detached, restarted.GetPeers().Single().Status);
    }

    /// <summary>開始時の相手が失われても成功条件を一台へ縮めません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task LostRequiredPeerReturnsUnknownThenNewMembershipCanSave()
    {
        int port = GetPort();
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), id);
        SqliteRepository firstStore = new(Path.Combine(path, "first.db"));
        SqliteRepository secondStore = new(Path.Combine(path, "second.db"));
        RequestTestClock clock = new();
        await using PeerCluster first = Create(id, "first", port, true, firstStore, clock);
        await first.StartAsync();
        PeerCluster second = Create(id, "second", port, false, secondStore);
        await second.StartAsync();
        await WaitAsync(() => first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);

        // 保存開始時の対象が確定するまでは、試験側の遅延で参加者を失効させません。
        clock.Freeze();
        await ((IAsyncDisposable)second).DisposeAsync();
        Guid failedId = Guid.NewGuid();
        Task<DurableReceipt> saving = first.ReplicateAsync(failedId, [new("configuration", "value", null, NodeValue.FromObject(1))]).AsTask();
        DurableReceipt lost;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
            string scope = "cluster:replication-members", key = failedId.ToString("N");
            DurableEntry? fixedMembers;
            while ((fixedMembers = await firstStore.ReadAsync(scope, key, timeout.Token)) is null)
            {
                await Task.Delay(50, timeout.Token);
            }
            Assert.Equal(2, fixedMembers.Value!.Json.GetArrayLength());
            clock.AdvanceAndResume(TimeSpan.Zero);
            await WaitAsync(() => first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 1);
            lost = await saving.ConfigureAwait(true);
        }
        finally
        {
            clock.AdvanceAndResume(TimeSpan.Zero);
            await saving.ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Unknown, lost.Outcome);
        Assert.Equal(EOutcome.Unknown, (await first.GetOperationAsync(failedId))!.Outcome);
        Assert.Null(await firstStore.ReadAsync("configuration", "value"));
        Assert.Equal(EOutcome.Succeeded, (await first.ReplicateAsync(Guid.NewGuid(), [new("configuration", "value", null, NodeValue.FromObject(2))])).Outcome);
        Assert.Equal("Persistence.Aborted", (await firstStore.CommitAsync(failedId)).Reason);
        await using PeerCluster restarted = Create(id, "second", port, false, secondStore);
        await restarted.StartAsync();
        Assert.Equal(EPeerStatus.Syncing, restarted.GetPeers().Single(peer => peer.InstanceId == restarted.LocalInstanceId).Status);
        await WaitAsync(() => restarted.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        Assert.Equal(2, (await secondStore.ReadAsync("configuration", "value"))!.Value!.Json.GetInt32());
    }

    /// <summary>生存する同Identityを後発起動で上書きしません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task DuplicateIdentityKeepsKnownParticipantAndRejectsLaterInstance()
    {
        int port = GetPort();
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), id);
        await using PeerCluster first = Create(id, "same", port, true, new SqliteRepository(Path.Combine(path, "first.db")));
        await first.StartAsync();
        await Task.Delay(50);
        await using PeerCluster later = Create(id, "same", port, true, new SqliteRepository(Path.Combine(path, "second.db")));
        await later.StartAsync();
        await WaitAsync(() => later.GetPeers().Single().Status == EPeerStatus.Conflict);
        Assert.Equal(EPeerStatus.Ready, first.GetPeers().Single().Status);
    }

    /// <summary>不正なfragmentを受信しても通常受信を失いません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task MalformedFragmentsDoNotStopDiscovery()
    {
        int port = GetPort();
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), id);
        await using PeerCluster first = Create(id, "first", port, true, new SqliteRepository(Path.Combine(path, "first.db")));
        await first.StartAsync();
        using UdpClient poison = new(new IPEndPoint(IPAddress.Loopback, 0)) { EnableBroadcast = true };
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("{\"Id\":\"" + Guid.NewGuid() + "\",\"Index\":0,\"Count\":1,\"Length\":2,\"Hash\":null,\"Payload\":null}");
        await poison.SendAsync(bytes, new IPEndPoint(IPAddress.Parse("127.255.255.255"), port));
        await using PeerCluster second = Create(id, "second", port, false, new SqliteRepository(Path.Combine(path, "second.db")));
        await second.StartAsync();
        await WaitAsync(() => first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
    }

    /// <summary>同じ旧版を持つ要求が並んでも、敗者のprepare/abortを各Peerへ配送しません。</summary>
    /// <returns>一つの成功だけが複製操作記録へ入り、全要求の比較結果は確定することです。</returns>
    [Fact]
    public async Task ConcurrentStaleStateWritesDoNotCreateReplicationOperations()
    {
        int port = GetPort();
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), id);
        SqliteRepository repository = new(Path.Combine(path, "first.db"));
        await using PeerCluster first = Create(id, "first", port, true, repository);
        NodeStateStore state = new(repository, "first");
        using StateReplication replication = new(first, repository, state);
        await first.StartAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        Task<Contracts.State.StateWriteResult>[] requests = Enumerable.Range(0, 16).Select(index => state.CompareExchangeAsync("workflow:burst", "generation", null,
            NodeValue.FromObject(index), deadline.Token).AsTask()).ToArray();
        Contracts.State.StateWriteResult[] results = await Task.WhenAll(requests);
        Assert.Single(results, result => result.Applied);
        Assert.All(results.Where(result => !result.Applied), result =>
        {
            Assert.Equal(EOutcome.Failed, result.Outcome);
            Assert.Equal("Persistence.VersionConflict", result.Reason);
            Assert.NotNull(result.Current);
        });
        Assert.Single(await repository.ListAsync("cluster:operations", deadline.Token));
        Assert.Single(await repository.ListAsync("cluster:replication-members", deadline.Token));
    }

    /// <summary>起動時の実必要条件を明示します。</summary>
    /// <returns>検証用Peer。</returns>
    private static PeerCluster Create(string id, string name, int port, bool fresh, IDurableRepository repository, TimeProvider? clock = null)
    {
        ClusterOptions options = new(id, name, name, "127.0.0.1", "127.255.255.255", port, 1, fresh, Path.GetTempPath())
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            PeerTimeout = TimeSpan.FromSeconds(2),
            RequestTimeout = TimeSpan.FromSeconds(4),
        };
        return new PeerCluster(options, repository, clock) { ReadinessCheck = token => ValueTask.FromResult(true) };
    }

    /// <summary>空いているUDPポートを選びます。</summary>
    /// <returns>ポート。</returns>
    private static int GetPort()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>実通信状態の成立を期限付きで待ちます。</summary>
    /// <returns>待機完了。</returns>
    private static async Task WaitAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }
}
