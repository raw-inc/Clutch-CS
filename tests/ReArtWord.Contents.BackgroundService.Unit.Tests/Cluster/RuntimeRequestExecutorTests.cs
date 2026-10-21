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

/// <summary>実行記録を後で保存しても、一度の副作用と結果不明の境界を維持します。</summary>
public sealed class RuntimeRequestExecutorTests
{
    /// <summary>同じIDの記録方式だけを変えても、既に受け付けた処理を別経路で実行しません。</summary>
    /// <returns>同期・非同期双方を入口にした内容不一致の拒否です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangingReceiptModeCannotRepeatAnAcceptedOperation(bool bufferedFirst)
    {
        BlockedRepository repository = new();
        repository.Release.TrySetResult();
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        Guid instance = Guid.NewGuid();
        RemoteExecutionRequest node = new(Guid.CreateVersion7(), Guid.NewGuid(), "wf", "revision", new NodeDefinition("node", "test", NodeSettings.Schema("{}")),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal), "p", instance, DateTimeOffset.UtcNow.AddMinutes(5), BufferedReceipt: bufferedFirst);
        ClusterRequest request = new(node.Id, "node.execute", "p", instance, node.ExpiresAt, NodeValue.FromObject(node));
        int effects = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        }
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Handler, true, true)).Outcome);
        ClusterRequest changed = request with { Payload = NodeValue.FromObject(node with { BufferedReceipt = !bufferedFirst }) };
        Assert.Equal("Execution.RequestMismatch", (await executor.ExecuteAsync(changed, Handler, true, true)).Reason);
        Assert.Equal(1, effects);
        await executor.DrainAsync();
    }

    /// <summary>ディスクが停止しても確認済みの結果を返し、並行再送は同じ一回へ合流します。</summary>
    /// <returns>完了前・保存後・再起動相当で副作用を再実行しない確認です。</returns>
    [Fact]
    public async Task CompletionDoesNotWaitForDiskAndDuplicateRequestsShareTheResult()
    {
        BlockedRepository repository = new();
        RuntimeRequestExecutor executor = new(repository, "receipts:p", TimeProvider.System);
        ClusterRequest request = Request();
        int effects = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(42)));
        }
        try
        {
            ClusterResponse result = await executor.ExecuteAsync(request, Handler, true, true).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(repository.Saved);
            Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Handler, true, true)).Outcome);
            Assert.Equal("Execution.RequestMismatch", (await executor.ExecuteAsync(request with { Payload = NodeValue.FromObject(2) }, Handler, true, true)).Reason);
            Assert.Equal(EOutcome.Succeeded, executor.Read(new(request.Id, ContractJson.Hash(request)))!.Response!.Outcome);
            Assert.Equal(0, repository.Reads);
        }
        finally
        {
            repository.Release.TrySetResult();
            await executor.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(repository.Saved);
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Handler, true, true)).Outcome);
        RuntimeRequestExecutor restarted = new(repository, "receipts:p", TimeProvider.System);
        Assert.Equal(EOutcome.Succeeded, (await restarted.ExecuteAsync(request, Handler, false, true)).Outcome);
        Assert.Equal(1, effects);
    }

    /// <summary>保存失敗でも同じ要求を処理し直さず、起動実体を失った結果を未知とします。</summary>
    /// <returns>一回の副作用と、保存失敗後の明示的な未確認結果です。</returns>
    [Fact]
    public async Task FailedJournalAndLostInstanceNeverReplayTheOperation()
    {
        BlockedRepository repository = new() { Fail = true };
        RuntimeRequestExecutor executor = new(repository, "receipts:p", TimeProvider.System);
        ClusterRequest request = Request();
        int effects = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        }
        Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Handler, true, true)).Outcome);
        repository.Release.TrySetResult();
        await executor.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(EOutcome.Unknown, (await executor.ExecuteAsync(request, Handler, true, true)).Outcome);
        RuntimeRequestExecutor restarted = new(repository, "receipts:p", TimeProvider.System);
        Assert.Equal(EOutcome.Unknown, (await restarted.ExecuteAsync(request, Handler, false, true)).Outcome);
        Assert.Equal(1, effects);
    }

    /// <summary>期限を本文だけで延長した古いIDを、照合保持の解放後に新しい操作として扱いません。</summary>
    /// <returns>保存アクセスも副作用もない開始前拒否です。</returns>
    [Fact]
    public async Task ExtendedExpiryCannotReviveAnOldRuntimeId()
    {
        BlockedRepository repository = new();
        RuntimeRequestExecutor executor = new(repository, "receipts:p", TimeProvider.System);
        ClusterRequest request = Request() with { Id = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-7)) };
        ClusterResponse result = await executor.ExecuteAsync(request, (_, _) => throw new InvalidOperationException("must not execute"), true, true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("Execution.RuntimeDeadlineInvalid", result.Reason);
        Assert.Equal(0, repository.Reads);
    }

    /// <summary>保存が詰まった場合は受付上限で副作用前に拒否し、待機結果を無制限に増やしません。</summary>
    /// <returns>有限受付と、保存再開後の全記録の解放です。</returns>
    [Fact]
    public async Task BlockedStorageRejectsNewEffectsAtTheAdmissionLimit()
    {
        BlockedRepository repository = new();
        RuntimeRequestExecutor executor = new(repository, "receipts:p", TimeProvider.System);
        int effects = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
        }
        try
        {
            for (int index = 0; index < 512; index++)
            {
                Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(Request(), Handler, true, true)).Outcome);
            }
            ClusterResponse refused = await executor.ExecuteAsync(Request(), Handler, true, true);
            Assert.Equal(EOutcome.NotExecuted, refused.Outcome);
            Assert.Equal("Execution.BufferedReceiptCapacity", refused.Reason);
            Assert.Equal(512, effects);
        }
        finally
        {
            repository.Release.TrySetResult();
            await executor.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(512, (await repository.ListAsync("receipts:p")).Count);
        System.Text.Json.JsonElement status = System.Text.Json.JsonSerializer.SerializeToElement(executor.Statistics);
        Assert.Equal(0, status.GetProperty("pending").GetInt32());
        Assert.Equal(0, status.GetProperty("retainedBytes").GetInt64());
    }

    /// <summary>停止開始後の拒否は、受付枠の消費や処理の再実行を伴いません。</summary>
    /// <returns>空の受付枠でも停止を優先し、開始済みの一件だけを保存する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownRejectsNewRequestsAndDrainsOnlyPreviouslyAcceptedEffects(bool active)
    {
        BlockedRepository repository = new();
        repository.Release.TrySetResult();
        RuntimeRequestExecutor executor = new(repository, "receipts:p", TimeProvider.System);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClusterRequest original = Request();
        int expectedEffects = active ? 1
                                     : 0;
        int effects = 0;
        async Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            await finish.Task.WaitAsync(token).ConfigureAwait(true);
            return new(EOutcome.Succeeded, NodeValue.FromObject("finished"));
        }
        if (active)
        {
            Assert.Equal(EOutcome.Unknown, (await executor.ExecuteAsync(original, Handler, true, false).ConfigureAwait(true)).Outcome);
        }
        Task drained = executor.DrainAsync();
        try
        {
            ClusterResponse refused = await executor.ExecuteAsync(Request(), Handler, true, true).ConfigureAwait(true);
            Assert.Equal(EOutcome.NotExecuted, refused.Outcome);
            Assert.Equal("Execution.Stopping", refused.Reason);
            Assert.Equal(expectedEffects, effects);
            System.Text.Json.JsonElement status = System.Text.Json.JsonSerializer.SerializeToElement(executor.Statistics);
            Assert.Equal(0, status.GetProperty("pending").GetInt32());
            Assert.Equal(expectedEffects, status.GetProperty("running").GetInt32());
            Assert.True(status.GetProperty("stopping").GetBoolean());
            Assert.Equal(0, status.GetProperty("retainedBytes").GetInt64());
            Assert.Equal(!active, drained.IsCompleted);
        }
        finally
        {
            finish.TrySetResult();
            await drained.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        }
        Assert.Equal(expectedEffects, (await repository.ListAsync("receipts:p").ConfigureAwait(true)).Count);
        if (active)
        {
            RuntimeRequestExecutor restarted = new(repository, "receipts:p", TimeProvider.System);
            Assert.Equal(EOutcome.Succeeded, (await restarted.ExecuteAsync(original, Handler, false, true).ConfigureAwait(true)).Outcome);
            Assert.Equal(1, effects);
            await restarted.DrainAsync().ConfigureAwait(true);
        }
    }

    /// <summary>現実の起動実体と生成時刻を固定した有限要求です。</summary>
    /// <returns>通常の実行要求です。</returns>
    private static ClusterRequest Request() => new(Guid.CreateVersion7(), "runtime.test", "p", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), NodeValue.Null);

    /// <summary>実処理と非同期永続保存の進行を試験側で独立して制御します。</summary>
    private sealed class BlockedRepository : DurableRepository, IRequestReceiptStore
    {
        private readonly Lock gate = new();
        private readonly RepositoryDocument document = new();

        internal bool Fail
        {
            get; init;
        }

        internal bool Saved
        {
            get; private set;
        }

        internal int Reads
        {
            get; private set;
        }

        internal TaskCompletionSource Entered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>新規実行の前にディスク照会が発生しないことを数えます。</summary>
        /// <returns>実際の保存値です。</returns>
        public override ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(scope, key, cancellationToken);
        }

        /// <summary>明示した保存枠まで確定応答を止めます。</summary>
        /// <returns>解放後の耐久CASです。</returns>
        async ValueTask<DurableEntry?> IRequestReceiptStore.CompareExchangeReceiptAsync(string scope, string key, string? expectedVersion, NodeValue value, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Fail)
            {
                throw new IOException("fixture disk unavailable");
            }
            DurableReceipt receipt = await base.ApplyBatchAsync(Guid.NewGuid(), [new(scope, key, expectedVersion, value)], cancellationToken).ConfigureAwait(false);
            Saved = true;
            return Assert.Single(receipt.Entries);
        }

        /// <summary>既存の保存規則を一取引ずつ適用します。</summary>
        /// <returns>取引の結果です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write)
        {
            using (gate.EnterScope())
            {
                return operation(document);
            }
        }
    }
}
