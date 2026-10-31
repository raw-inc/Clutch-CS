using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>実保存先の通知が、未確定書込み・別キー・保存済みnullを混同しないことを確認します。</summary>
public sealed class StateObservationTests
{
    /// <summary>SQLiteとJSONの両方で、確定変更だけを待ち受け、同値保存は通知しません。</summary>
    /// <returns>初期値、更新、明示null、削除、取消の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchReportsCommittedValuesAndDeletionAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Create(files.Root, json);
        NodeStateStore store = new(repository, "a");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        IAsyncEnumerator<StateChange> watch = store.WatchAsync("workflow:one", "key", timeout.Token).GetAsyncEnumerator();
        await using ConfiguredAsyncDisposable cleanup = watch.ConfigureAwait(true);
        Assert.True(await watch.MoveNextAsync().ConfigureAwait(true));
        Assert.True(watch.Current.Initial);
        Assert.Null(watch.Current.Value);
        Task<bool> next = watch.MoveNextAsync().AsTask();
        await using ConfiguredAsyncDisposable valueLifetime = new TestTaskLifetime(next, timeout).ConfigureAwait(true);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:two", "key", null, NodeValue.FromObject(99))], timeout.Token).ConfigureAwait(true);
        Guid aborted = Guid.NewGuid();
        await repository.PrepareAsync(aborted, [new("workflow:one", "key", null, NodeValue.FromObject(88))], timeout.Token).ConfigureAwait(true);
        await repository.AbortPreparedAsync(aborted, timeout.Token).ConfigureAwait(true);
        Assert.False(next.IsCompleted);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:one", "key", null, NodeValue.FromObject(1))], timeout.Token).ConfigureAwait(true);
        Assert.True(await next.WaitAsync(timeout.Token).ConfigureAwait(true));
        Assert.Equal(1, watch.Current.Value!.Json.GetInt32());
        next = watch.MoveNextAsync().AsTask();
        await using ConfiguredAsyncDisposable nullLifetime = new TestTaskLifetime(next, timeout).ConfigureAwait(true);
        DurableEntry before = (await repository.ReadAsync("workflow:one", "key", timeout.Token).ConfigureAwait(true))!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(before.Scope, before.Key, before.Version, NodeValue.FromObject(1))], timeout.Token).ConfigureAwait(true);
        before = (await repository.ReadAsync(before.Scope, before.Key, timeout.Token).ConfigureAwait(true))!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(before.Scope, before.Key, before.Version, NodeValue.Null)], timeout.Token).ConfigureAwait(true);
        Assert.True(await next.WaitAsync(timeout.Token).ConfigureAwait(true));
        Assert.NotNull(watch.Current.Value);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, watch.Current.Value.Json.ValueKind);
        next = watch.MoveNextAsync().AsTask();
        await using ConfiguredAsyncDisposable deletionLifetime = new TestTaskLifetime(next, timeout).ConfigureAwait(true);
        before = (await repository.ReadAsync(before.Scope, before.Key, timeout.Token).ConfigureAwait(true))!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(before.Scope, before.Key, before.Version, null)], timeout.Token).ConfigureAwait(true);
        Assert.True(await next.WaitAsync(timeout.Token).ConfigureAwait(true));
        Assert.False(watch.Current.Initial);
        Assert.Null(watch.Current.Value);
        next = watch.MoveNextAsync().AsTask();
        await using ConfiguredAsyncDisposable cancellationLifetime = new TestTaskLifetime(next, timeout).ConfigureAwait(true);
        await timeout.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next).ConfigureAwait(true);
    }

    /// <summary>初期読取りと同時に進んだ保存を、古い初期値で上書きしません。</summary>
    /// <returns>登録後の競合書込みと購読解除の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialSnapshotIncludesRacingCommitAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Create(files.Root, json);
        using DurableStateSubscription watch = ((IDurableStateChanges)repository).SubscribeState("state:cluster", "key");
        DurableEntry? baseline = await repository.ReadAsync("state:cluster", "key").ConfigureAwait(true);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("state:cluster", "key", null, NodeValue.FromObject(42))]).ConfigureAwait(true);
        Assert.Equal(42, watch.Initialize(baseline)!.Value!.Json.GetInt32());
        watch.Dispose();
        Assert.Throws<ObjectDisposedException>(() => watch.Initialize(null));
    }

    /// <summary>遅い購読の上限を超えても、状態保存自体は成功し続けます。</summary>
    /// <returns>明示的な購読失敗と最終保存値の検証です。</returns>
    [Fact]
    public async Task SlowWatchCannotBlockWritesAsync()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Create(files.Root, false);
        using DurableStateSubscription watch = ((IDurableStateChanges)repository).SubscribeState("state:cluster", "key");
        watch.Initialize(null);
        string? version = null;
        for (int index = 0; index < 260; index++)
        {
            DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.NewGuid(), [new("state:cluster", "key", version, NodeValue.FromObject(index))]).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, receipt.Outcome);
            version = receipt.Entries.Single().Version;
        }
        for (int index = 0; index < 256; index++)
        {
            Assert.Equal(index, (await watch.ReadAsync().ConfigureAwait(true)).Value!.Json.GetInt32());
        }
        Exception failed = await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(async () => await watch.ReadAsync().ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal("State.ChangeQueueOverflow", failed.InnerException?.Message);
        Assert.Equal(259, (await repository.ReadAsync("state:cluster", "key").ConfigureAwait(true))!.Value!.Json.GetInt32());
    }

    /// <summary>隔離された実保存先を作ります。</summary>
    /// <returns>同じ外部契約を持つ保存媒体です。</returns>
    private static IDurableRepository Create(string root, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(Path.Combine(root, "state.json"));
        }
        return new SqliteRepository(Path.Combine(root, "state.db"));
    }
}
