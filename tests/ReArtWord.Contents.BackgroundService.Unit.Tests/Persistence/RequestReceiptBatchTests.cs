using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>受付の合流でも個別CAS、耐久確認、取消と失敗の境界を維持します。</summary>
public sealed class RequestReceiptBatchTests
{
    /// <summary>保存枠の待機中に到着した独立した受付を一度で保存し、確定前には誰も成功しません。</summary>
    /// <returns>再読込みできる個別結果と、一取引内の重複キー拒否です。</returns>
    [Fact]
    public async Task ConcurrentReceiptsWaitForCommitAndKeepIndividualComparisons()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "receipts.db");
        SqliteRepository repository = new(path);
        RequestReceiptBatch batch = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int transactions = 0;
        async ValueTask<IReadOnlyList<DurableEntry?>> Apply(Func<IReadOnlyList<ReceiptWrite>> take, CancellationToken token)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            Interlocked.Increment(ref transactions);
            return repository.CompareExchangeReceipts(take(), token);
        }
        string duplicate = Guid.NewGuid().ToString("N");
        List<Task<DurableEntry?>> requests = [batch.WriteAsync(new("receipts:p", duplicate, null, NodeValue.FromObject(1)), Apply).AsTask()];
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requests.Add(batch.WriteAsync(new("receipts:p", duplicate, null, NodeValue.FromObject(2)), Apply).AsTask());
        for (int index = 0; index < 62; index++)
        {
            requests.Add(batch.WriteAsync(new("receipts:p", Guid.NewGuid().ToString("N"), null, NodeValue.FromObject(index)), Apply).AsTask());
        }
        Assert.All(requests, request => Assert.False(request.IsCompleted));
        Assert.Empty(await repository.ListAsync("receipts:p"));
        release.TrySetResult();
        DurableEntry?[] saved = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, transactions);
        Assert.NotNull(saved[0]);
        Assert.Null(saved[1]);
        Assert.Equal(63, saved.OfType<DurableEntry>().Count());
        SqliteRepository reopened = new(path);
        Assert.Equal(63, (await reopened.ListAsync("receipts:p")).Count);
        Assert.Equal(1, (await reopened.ReadAsync("receipts:p", duplicate))!.Value!.Json.GetInt32());
    }

    /// <summary>保存枠の取得失敗で待機者を残さず、取消した一件は次の取引へ混ぜません。</summary>
    /// <returns>全待機者への失敗通知と次回受付の正常終了です。</returns>
    [Fact]
    public async Task FailedAdmissionReleasesEveryWaiterAndCancelledReceiptIsNotWritten()
    {
        RequestReceiptBatch batch = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<IReadOnlyList<DurableEntry?>> Fail(Func<IReadOnlyList<ReceiptWrite>> take, CancellationToken token)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            throw new IOException("fixture storage unavailable before taking batch");
        }
        ReceiptWrite first = new("receipts:p", Guid.NewGuid().ToString("N"), null, NodeValue.FromObject(1));
        Task<DurableEntry?> owner = batch.WriteAsync(first, Fail).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<DurableEntry?> other = batch.WriteAsync(first with { Key = Guid.NewGuid().ToString("N") }, Fail).AsTask();
        using CancellationTokenSource cancellation = new();
        Task<DurableEntry?> cancelled = batch.WriteAsync(first with { Key = Guid.NewGuid().ToString("N") }, Fail, cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        release.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => owner.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<IOException>(() => other.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        ValueTask<IReadOnlyList<DurableEntry?>> Save(Func<IReadOnlyList<ReceiptWrite>> take, CancellationToken token)
        {
            ReceiptWrite only = Assert.Single(take());
            return ValueTask.FromResult<IReadOnlyList<DurableEntry?>>([new(only.Scope, only.Key, "confirmed", only.Value)]);
        }
        Assert.NotNull(await batch.WriteAsync(first, Save));
    }
}
