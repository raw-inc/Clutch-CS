using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>永続受付の遅延と保存例外を、副作用の再実行や定期照会増幅なしで扱います。</summary>
public sealed class RequestExecutorTests
{
    /// <summary>短い遠隔処理は次の定期照会を待たず、保存した確定結果を最初の応答に含めます。</summary>
    /// <returns>完了保存後の結果と、一回だけの実行です。</returns>
    [Fact]
    public async Task ShortRemoteOperationReturnsPersistedCompletionDirectly()
    {
        RequestRepository repository = new() { DelayClaim = true };
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        ClusterRequest request = new(Guid.NewGuid(), "test", "p", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null);
        int effects = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(42)));
        }
        Task<ClusterResponse> response = executor.ExecuteAsync(request, Handler, true, false);
        Assert.False(response.IsCompleted);
        repository.Claim.TrySetResult();
        ClusterResponse completed = await response.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(EOutcome.Succeeded, completed.Outcome);
        Assert.Equal(42, completed.Value!.Json.GetInt32());
        Assert.Equal(EOutcome.Succeeded, (await executor.ReadStatusAsync(new(request.Id, ContractJson.Hash(request)))).Response!.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Handler, true, false)).Outcome);
        Assert.Equal(1, effects);
    }

    /// <summary>応答の待機取消で、受付済み操作を取消したり同じ副作用を再実行しません。</summary>
    /// <returns>元の処理だけが継続し、保存された結果を再照会できます。</returns>
    [Fact]
    public async Task CancellingShortCompletionWaitDoesNotCancelOwnedOperation()
    {
        RequestRepository repository = new();
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        ClusterRequest request = new(Guid.NewGuid(), "test", "p", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource caller = new();
        int effects = 0;
        async Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);

            // 50ミリ秒の応答期限と競合しないよう、受付後、完了待ちへ戻る前に取り消します。
            await caller.CancelAsync().ConfigureAwait(false);
            Assert.False(token.IsCancellationRequested);
            return await FinishAsync(release.Task, token).ConfigureAwait(false);
        }
        Task<ClusterResponse> response = executor.ExecuteAsync(request, Handler, true, false, caller.Token);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
            Assert.True((await executor.ReadStatusAsync(new(request.Id, ContractJson.Hash(request)))).Running);
        }
        finally
        {
            release.TrySetResult();
            await executor.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Handler, true, false)).Outcome);
        Assert.Equal(1, effects);
    }

    /// <summary>ノード実行でも操作記録照会パケットの一時欠測を終端失敗へ丸めません。</summary>
    /// <returns>実UDPで元結果だけを照会し続け、一度の副作用を確認します。</returns>
    [Fact]
    public async Task RemoteNodeRecoversFromTemporaryReceiptReadFailure()
    {
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string clusterId = Guid.NewGuid().ToString("N");
        RequestRepository firstRepository = new();
        RequestRepository secondRepository = new();
        PeerCluster first = new(new ClusterOptions(clusterId, "first", "first", "127.0.0.1", "127.255.255.255", port, 1, true, Path.GetTempPath()) { RequestTimeout = TimeSpan.FromMilliseconds(200), HeartbeatInterval = TimeSpan.FromMilliseconds(50) }, firstRepository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        PeerCluster second = new(new ClusterOptions(clusterId, "second", "second", "127.0.0.1", "127.255.255.255", port, 1, false, Path.GetTempPath()) { RequestTimeout = TimeSpan.FromMilliseconds(200), HeartbeatInterval = TimeSpan.FromMilliseconds(50) }, secondRepository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        int effects = 0;
        second.RemoteExecutionHandler = async (_, token) =>
        {
            Interlocked.Increment(ref effects);
            await Task.Delay(150, token).ConfigureAwait(false);
            secondRepository.FailReadsUntil = DateTimeOffset.UtcNow.AddMilliseconds(900);
            return NodeResult.Success(NodeValue.FromObject(42));
        };
        await first.StartAsync();
        await second.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token);
        }
        RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "test", "revision", new NodeDefinition("node", "test", NodeSettings.Schema("{}")), new Dictionary<string, NodeValue>(StringComparer.Ordinal), "second", second.LocalInstanceId, DateTimeOffset.UtcNow.AddSeconds(1));
        NodeResult result = await first.ExecuteAsync(request, timeout.Token);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, effects);
        Assert.True(0 < secondRepository.FailedReads);
    }

    /// <summary>受付前の同じ要求も共有し、異内容を拒否して操作記録照会を早期終了させません。</summary>
    /// <returns>一回の副作用と保存定期照会なしの受付確認です。</returns>
    [Fact]
    public async Task PendingAdmissionSharesCompletionWithoutPollingStorage()
    {
        RequestRepository repository = new() { DelayClaim = true };
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        ClusterRequest request = new(Guid.NewGuid(), "test", "p", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int effects = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return FinishAsync(release.Task, token);
        }
        Task<ClusterResponse>[] admissions = Enumerable.Range(0, 4).Select(_ => executor.ExecuteAsync(request, Handler, true, false)).ToArray();
        Assert.All(admissions, task => Assert.False(task.IsCompleted));
        Assert.True((await executor.ReadStatusAsync(new(request.Id, ContractJson.Hash(request)))).Running);
        Assert.Equal(1, repository.Reads);
        ClusterResponse mismatch = await executor.ExecuteAsync(request with { Payload = NodeValue.FromObject(2) }, Handler, true, false);
        Assert.Equal("Execution.RequestMismatch", mismatch.Reason);
        repository.Claim.TrySetResult();
        try
        {
            foreach (ClusterResponse admission in await Task.WhenAll(admissions).WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.Equal("Execution.Running", admission.Reason);
            }
            Assert.Equal(1, effects);
            Assert.Equal(1, repository.Reads);
        }
        finally
        {
            release.TrySetResult();
            await executor.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(EOutcome.Succeeded, (await executor.ReadStatusAsync(new(request.Id, ContractJson.Hash(request)))).Response!.Outcome);
    }

    /// <summary>開始または完了の保存例外でもfaulted タスクを保持せず、永続開始から再実行しません。</summary>
    /// <returns>元要求の安全な未知状態と所有タスクの解放です。</returns>
    [Theory]
    [InlineData(1, true, 0)]
    [InlineData(2, false, 1)]
    public async Task PersistenceExceptionsReleaseOwnedTaskWithoutRepeatingEffects(int faultAt, bool afterWrite, int expectedEffects)
    {
        RequestRepository repository = new() { FaultAt = faultAt, AfterWrite = afterWrite };
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        ClusterRequest request = new(Guid.NewGuid(), "test", "p", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null);
        int effects = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        }
        Assert.Equal(EOutcome.Unknown, (await executor.ExecuteAsync(request, Handler, true, true)).Outcome);
        await executor.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Execution.StartedWithoutResult", (await executor.ExecuteAsync(request, Handler, true, true)).Reason);
        Assert.False((await executor.ReadStatusAsync(new(request.Id, ContractJson.Hash(request)))).Running);
        Assert.Equal(expectedEffects, effects);
    }

    /// <summary>受付確認と完了確認の境界を試験側から制御します。</summary>
    /// <returns>解放後の成功結果です。</returns>
    private static async Task<ClusterResponse> FinishAsync(Task release, CancellationToken cancellationToken = default)
    {
        await release.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(EOutcome.Succeeded);
    }

    /// <summary>既存の保存規則を使い、特定の永続境界だけを遅延・失敗させます。</summary>
    private sealed class RequestRepository : DurableRepository
    {
        private readonly RepositoryDocument document = new();
        private readonly Lock gate = new();
        private int writes;

        internal bool DelayClaim
        {
            get; init;
        }

        internal int FaultAt
        {
            get; init;
        }

        internal bool AfterWrite
        {
            get; init;
        }

        internal int Reads
        {
            get; private set;
        }

        internal TaskCompletionSource Claim
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal DateTimeOffset FailReadsUntil
        {
            get; set;
        }

        internal int FailedReads
        {
            get; private set;
        }

        /// <summary>読取りの増幅を数えます。</summary>
        /// <returns>既存の保存値です。</returns>
        public override ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
        {
            Reads++;
            string receiptScope = "receipts:second";
            if (DateTimeOffset.UtcNow < FailReadsUntil && string.Equals(scope, receiptScope, StringComparison.Ordinal))
            {
                FailedReads++;
                throw new IOException("fixture transient read failure");
            }
            return base.ReadAsync(scope, key, cancellationToken);
        }

        /// <summary>指定した保存境界で例外を注入します。</summary>
        /// <returns>実際の保存結果です。</returns>
        public override async ValueTask<DurableReceipt> ApplyBatchAsync(Guid operationId, IReadOnlyList<DurableMutation> mutations, CancellationToken cancellationToken = default)
        {
            int current = Interlocked.Increment(ref writes);
            if (DelayClaim && current == 1)
            {
                await Claim.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (current == FaultAt && !AfterWrite)
            {
                throw new IOException("fixture persistence failure");
            }
            DurableReceipt result = await base.ApplyBatchAsync(operationId, mutations, cancellationToken).ConfigureAwait(false);
            if (current == FaultAt)
            {
                throw new IOException("fixture lost persistence acknowledgement");
            }
            return result;
        }

        /// <summary>一取引の既存意味を維持した有限試験用の構成です。</summary>
        /// <returns>取引結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                return operation(document);
            }
        }
    }
}
