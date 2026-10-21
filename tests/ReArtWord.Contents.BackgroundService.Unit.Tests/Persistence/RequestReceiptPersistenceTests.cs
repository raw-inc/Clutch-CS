using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>受付の重複履歴を省いても、耐久受付・版比較・再実行防止を維持します。</summary>
public sealed class RequestReceiptPersistenceTests
{
    /// <summary>一要求一行で保存し、再作成後も同じ結果と内容不一致の拒否を維持します。</summary>
    /// <returns>二媒体と用途別routingでの実保存確認です。</returns>
    [Theory]
    [InlineData(EStorageBackend.Sqlite)]
    [InlineData(EStorageBackend.Json)]
    public async Task CompletedRequestsKeepResultsWithoutHistoryOfTheirOwnReceipts(EStorageBackend backend)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "p", new(backend));
        int owners = (await repository.ListAsync("peer:persistence-routing")).Count;
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        List<ClusterRequest> requests = [];
        int effects = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref effects);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(42)));
        }
        for (int index = 0; index < 12; index++)
        {
            ClusterRequest request = NewRequest();
            requests.Add(request);
            Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Handler, true, true)).Outcome);
        }
        Assert.Equal(12, (await repository.ListAsync("receipts:p")).Count);
        Assert.Equal(owners, (await repository.ListAsync("peer:persistence-routing")).Count);
        IDurableRepository reopened = await RepositoryFactory.CreateAsync(files.Root, "p", new(backend));
        RequestExecutor restarted = new(reopened, "p", TimeProvider.System);
        foreach (ClusterRequest request in requests)
        {
            Assert.Equal(EOutcome.Succeeded, (await restarted.ExecuteAsync(request, Handler, true, true)).Outcome);
            Assert.Equal("Execution.RequestMismatch", (await restarted.ExecuteAsync(request with { Payload = NodeValue.FromObject(99) }, Handler, true, true)).Reason);
        }
        Assert.Equal(12, effects);
    }

    /// <summary>並行する別保存実体も一つだけが新規受付を取得します。</summary>
    /// <returns>古い版と予約済み項目を拒否する実CASです。</returns>
    [Fact]
    public async Task ReceiptCasRejectsConcurrentAndReservedWrites()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository first = new(path), second = new(path);
        string key = Guid.NewGuid().ToString("N");
        Task<DurableEntry?>[] tasks = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            SqliteRepository target = first;
            if (index % 2 != 0)
            {
                target = second;
            }
            return await RequestReceiptPersistence.WriteAsync(target, "receipts:p", key, null, NodeValue.FromObject(index)).ConfigureAwait(false);
        })).ToArray();
        DurableEntry entry = Assert.Single((await Task.WhenAll(tasks)).OfType<DurableEntry>());
        Assert.Null(await RequestReceiptPersistence.WriteAsync(first, "receipts:p", key, null, NodeValue.FromObject(99)));
        Guid reservation = Guid.NewGuid();
        Assert.Equal(EOutcome.Succeeded, (await first.PrepareAsync(reservation, [new("receipts:p", key, entry.Version, NodeValue.FromObject(2))])).Outcome);
        Assert.Null(await RequestReceiptPersistence.WriteAsync(second, "receipts:p", key, entry.Version, NodeValue.FromObject(3)));
        Assert.Equal("Persistence.Aborted", (await first.AbortPreparedAsync(reservation)).Reason);
        Assert.NotNull(await RequestReceiptPersistence.WriteAsync(second, "receipts:p", key, entry.Version, NodeValue.FromObject(4)));
    }

    /// <summary>受付・完了の応答を失っても、開始済みの副作用は再実行しません。</summary>
    /// <returns>実SQL保存前後の障害と再起動相当の確認です。</returns>
    [Theory]
    [InlineData(1, true, 0)]
    [InlineData(2, false, 1)]
    [InlineData(2, true, 1)]
    public async Task LostPersistenceAcknowledgementDoesNotRepeatEffects(int faultAt, bool afterWrite, int expectedEffects)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        RequestExecutor executor = new(new ReceiptFaults(store, faultAt, afterWrite), "p", TimeProvider.System);
        ClusterRequest request = NewRequest();
        int effects = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            effects++;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(7)));
        }
        Assert.Equal(EOutcome.Unknown, (await executor.ExecuteAsync(request, Handler, true, true)).Outcome);
        RequestExecutor restarted = new(new SqliteRepository(path), "p", TimeProvider.System);
        ClusterResponse replay = await restarted.ExecuteAsync(request, Handler, true, true);
        EOutcome expected = EOutcome.Unknown;
        if (faultAt == 2 && afterWrite)
        {
            expected = EOutcome.Succeeded;
        }
        Assert.Equal(expected, replay.Outcome);
        Assert.Equal(expectedEffects, effects);
        Assert.False((await restarted.ReadStatusAsync(new(request.Id, ContractJson.Hash(request)))).Running);
    }

    /// <summary>実行内容と対象を固定した要求です。</summary>
    /// <returns>期限内の独立した要求です。</returns>
    private static ClusterRequest NewRequest() => new(Guid.NewGuid(), "test", "p", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null);

    /// <summary>省履歴CASの確定前後だけを失敗させます。</summary>
    private sealed class ReceiptFaults(SqliteRepository inner, int faultAt, bool afterWrite) : DurableRepository, IRequestReceiptStore
    {
        private int writes;

        /// <summary>実際の記録を読んで再実行可否を判断させます。</summary>
        /// <returns>実SQLの項目です。</returns>
        public override ValueTask<DurableEntry?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default) => inner.ReadAsync(scope, key, cancellationToken);

        /// <summary>同じ永続CASを実行し、指定した地点で応答を失います。</summary>
        /// <returns>確定した項目、または保存結果不明の例外です。</returns>
        async ValueTask<DurableEntry?> IRequestReceiptStore.CompareExchangeReceiptAsync(string scope, string key, string? expectedVersion, NodeValue value, CancellationToken cancellationToken)
        {
            int attempt = ++writes;
            if (attempt == faultAt && !afterWrite)
            {
                throw new IOException("fixture before receipt write");
            }
            DurableEntry? result = await RequestReceiptPersistence.WriteAsync(inner, scope, key, expectedVersion, value, cancellationToken).ConfigureAwait(false);
            if (attempt == faultAt && afterWrite)
            {
                throw new IOException("fixture lost receipt acknowledgement");
            }
            return result;
        }

        /// <summary>省履歴経路が通常の全履歴書込みへ戻っていないことを確認します。</summary>
        /// <returns>このfixtureでは使用しません。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write) => throw new NotSupportedException();
    }
}
