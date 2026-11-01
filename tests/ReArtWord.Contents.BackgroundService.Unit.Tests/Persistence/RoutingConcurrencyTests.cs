using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>同じ保存先を複数実体から利用しても、待機取消と保存排他を維持します。</summary>
public sealed class RoutingConcurrencyTests
{
    /// <summary>ファイル排他の待機を一つへまとめ、取消済みの待機者が後続を塞ぎません。</summary>
    /// <returns>取消後も両リポジトリで確定値を共有できることです。</returns>
    [Theory]
    [InlineData(EStorageBackend.Sqlite)]
    [InlineData(EStorageBackend.Json)]
    public async Task CancelledWaitersDoNotBlockOtherRepositoryInstances(EStorageBackend backend)
    {
        using MediaTestFiles files = new();
        PersistenceOptions options = new(backend);
        IDurableRepository first = await RepositoryFactory.CreateAsync(files.Root, "a", options);
        IDurableRepository second = await RepositoryFactory.CreateAsync(files.Root, "a", options);
        using CancellationTokenSource cancelled = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        List<Task<DurableEntry?>> interrupted = [];
        List<Task<DurableReceipt>> pending = [];
        try
        {
            using (FileStream external = new(Path.Combine(files.Root, "routing.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                for (int index = 0; index < 32; index++)
                {
                    interrupted.Add(first.ReadAsync("workflow:test", "absent", cancelled.Token).AsTask());
                    pending.Add(second.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", index.ToString(System.Globalization.CultureInfo.InvariantCulture), null, NodeValue.FromObject(index))], deadline.Token).AsTask());
                }
                await cancelled.CancelAsync();
                foreach (Task<DurableEntry?> operation in interrupted)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
                }
                Assert.All(pending, operation => Assert.False(operation.IsCompleted));
            }
            DurableReceipt[] results = await Task.WhenAll(pending).WaitAsync(deadline.Token);
            Assert.All(results, result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
            Assert.Equal(32, (await first.ListAsync("workflow:test", deadline.Token)).Count);
            Assert.Equal(await first.GetSharedVersionAsync(deadline.Token), await second.GetSharedVersionAsync(deadline.Token));
        }
        finally
        {
            await Task.WhenAll(cancelled.CancelAsync(), deadline.CancelAsync()).ConfigureAwait(true);
            try
            {
                await Task.WhenAll(interrupted.Cast<Task>().Concat(pending)).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                // 試験終了時に取り消した待機も、保存先を削除する前に回収します。
            }
        }
    }

    /// <summary>所有情報を一括取得しても共有履歴を落とさず、ローカル履歴を持ち出しません。</summary>
    /// <returns>各バックエンドで同じ共有スナップショットの境界を維持することです。</returns>
    [Theory]
    [InlineData(EStorageBackend.Sqlite)]
    [InlineData(EStorageBackend.Json)]
    public async Task BatchedOwnerReadsKeepOnlySharedOperationHistory(EStorageBackend backend)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", new PersistenceOptions(backend));
        HashSet<Guid> shared = [];
        for (int index = 0; index < 32; index++)
        {
            Guid id = Guid.NewGuid();
            shared.Add(id);
            string key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(id, [new("workflow:test", key, null, NodeValue.FromObject(index))])).Outcome);
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("peer:a:private", key, null, NodeValue.FromObject(index))])).Outcome);
        }
        RepositorySnapshot exported = await repository.ExportAsync();
        Assert.Equal(32, exported.Entries.Count);
        Assert.True(shared.SetEquals(exported.Receipts.Select(receipt => receipt.OperationId)));
        Assert.True(shared.SetEquals(exported.OperationOwners!.Select(owner => owner.OperationId)));
        Assert.All(exported.OperationOwners!, owner => Assert.Equal("workflow:test", Assert.Single(owner.Scopes)));
    }

}
