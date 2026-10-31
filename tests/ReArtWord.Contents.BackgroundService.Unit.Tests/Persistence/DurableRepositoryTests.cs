using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>媒体を越える原子性と再起動後の確認結果を検証します。</summary>
public sealed class DurableRepositoryTests
{
    /// <summary>一括競合は他項目を書き換えず、同ID内容違いを拒否します。</summary>
    /// <returns>検証完了。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchConflictAndReceiptSurviveRestart(bool sqlite)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "store");
        IDurableRepository store = Create(path, sqlite);
        Guid operation = Guid.NewGuid();
        DurableMutation[] changes = [new("workflow:a", "x", null, NodeValue.FromObject(4)), new("workflow:a", "y", null, NodeValue.FromObject(5))];
        DurableReceipt first = await store.ApplyBatchAsync(operation, changes);
        Assert.Equal(EOutcome.Succeeded, first.Outcome);
        IDurableRepository restarted = Create(path, sqlite);
        DurableReceipt duplicate = await restarted.ApplyBatchAsync(operation, changes);
        Assert.Equal(first.Entries[0].Version, duplicate.Entries[0].Version);
        DurableReceipt mismatch = await restarted.ApplyBatchAsync(operation, [new("workflow:a", "x", null, NodeValue.FromObject(99))]);
        Assert.Equal("Persistence.OperationMismatch", mismatch.Reason);
        DurableReceipt conflict = await restarted.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:a", "x", null, NodeValue.FromObject(100)), new("workflow:a", "z", null, NodeValue.FromObject(8))]);
        Assert.Equal(EOutcome.Failed, conflict.Outcome);
        Assert.Null(await restarted.ReadAsync("workflow:a", "z"));
        Assert.Equal(4, (await restarted.ReadAsync("workflow:a", "x"))!.Value!.Json.GetInt32());
    }

    /// <summary>prepare中の別更新を拒否し、再起動後コミットします。</summary>
    /// <returns>検証完了。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedReservationsSurviveRestart(bool sqlite)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "store");
        IDurableRepository store = Create(path, sqlite);
        Guid operation = Guid.NewGuid();
        Assert.False(await store.HasPendingWriteAsync("configuration", "a"));
        Assert.Equal(EOutcome.Succeeded, (await store.PrepareAsync(operation, [new("configuration", "a", null, NodeValue.FromObject("first"))])).Outcome);
        Assert.NotNull(await store.ReadPreparationAsync(operation));
        Assert.Null(await store.ReadPreparationAsync(Guid.NewGuid()));
        Assert.True(await store.HasPendingWriteAsync("configuration", "a"));
        Assert.False(await store.HasPendingWriteAsync("configuration", "other"));
        Assert.False(await store.HasPendingWriteAsync("other", "a"));
        Assert.Null(await store.ReadAsync("configuration", "a"));
        Assert.Equal(EOutcome.Failed, (await store.ApplyBatchAsync(Guid.NewGuid(), [new("configuration", "a", null, NodeValue.FromObject("other"))])).Outcome);
        IDurableRepository restarted = Create(path, sqlite);
        Assert.True(await restarted.HasPendingWriteAsync("configuration", "a"));
        Assert.Equal(EOutcome.Succeeded, (await restarted.CommitAsync(operation)).Outcome);
        Assert.Null(await restarted.ReadPreparationAsync(operation));
        Assert.False(await restarted.HasPendingWriteAsync("configuration", "a"));
        Assert.Equal("first", (await restarted.ReadAsync("configuration", "a"))!.Value!.GetString());
    }

    /// <summary>破損保存先を空の正常保存先へ置換しません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task CorruptFileIsRejected()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "store");
        JsonFileRepository store = new(path);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:a", "x", null, NodeValue.FromBytes(new byte[] { 1, 2 }))]);
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.ReadAsync("workflow:a", "x").ConfigureAwait(false));
    }

    /// <summary>非競合変更は取り込み、分岐した同項目は自動上書きしません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task MergePreservesConflictingCandidates()
    {
        using MediaTestFiles files = new();
        IDurableRepository first = Create(Path.Combine(files.Root, "a"), true);
        IDurableRepository second = Create(Path.Combine(files.Root, "b"), true);
        DurableReceipt seed = await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:a", "x", null, NodeValue.FromObject(1))]);
        Assert.True(await second.ImportAsync(await first.ExportAsync()));
        await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:a", "x", seed.Entries[0].Version, NodeValue.FromObject(2))]);
        await second.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:a", "x", seed.Entries[0].Version, NodeValue.FromObject(3)), new("workflow:a", "y", null, NodeValue.FromObject(9))]);
        Assert.False(await first.ImportAsync(await second.ExportAsync()));
        Assert.Equal(2, (await first.ReadAsync("workflow:a", "x"))!.Value!.Json.GetInt32());
        Assert.Equal(9, (await first.ReadAsync("workflow:a", "y"))!.Value!.Json.GetInt32());
        IReadOnlyList<DurableEntry> candidates = await first.GetConflictsAsync("workflow:a");
        Assert.Equal(2, candidates.Count);
        Assert.Equal(EOutcome.Succeeded, (await first.ResolveConflictAsync(Guid.NewGuid(), "workflow:a", "x", candidates.Select(entry => entry.Version).ToArray(), NodeValue.FromObject(3))).Outcome);
        Assert.Empty(await first.GetConflictsAsync("workflow:a"));
        Assert.True(await second.ImportAsync(await first.ExportAsync()));
        Assert.Equal(3, (await second.ReadAsync("workflow:a", "x"))!.Value!.Json.GetInt32());
    }

    /// <summary>指定媒体を作成します。</summary>
    /// <returns>保存先。</returns>
    private static IDurableRepository Create(string path, bool sqlite)
    {
        if (sqlite)
        {
            return new SqliteRepository(path);
        }
        return new JsonFileRepository(path);
    }

    /// <summary>中止後は新操作を受理し、古いコミットは遅れても適用しません。</summary>
    /// <returns>検証完了。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortReleasesReservationAndRejectsDelayedCommit(bool sqlite)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "store");
        IDurableRepository store = Create(path, sqlite);
        Guid oldId = Guid.NewGuid();
        await store.PrepareAsync(oldId, [new("configuration", "a", null, NodeValue.FromObject(1))]);
        Assert.True(await store.HasPendingWriteAsync("configuration", "a"));
        Assert.Equal("Persistence.Aborted", (await store.AbortPreparedAsync(oldId)).Reason);
        Assert.False(await store.HasPendingWriteAsync("configuration", "a"));
        Guid newId = Guid.NewGuid();
        Assert.Equal(EOutcome.Succeeded, (await store.ApplyBatchAsync(newId, [new("configuration", "a", null, NodeValue.FromObject(2))])).Outcome);
        IDurableRepository restarted = Create(path, sqlite);
        Assert.Equal("Persistence.Aborted", (await restarted.CommitAsync(oldId)).Reason);
        Assert.Equal(2, (await restarted.ReadAsync("configuration", "a"))!.Value!.Json.GetInt32());
    }

    /// <summary>初期化済みSQLiteの喪失を空保存先として受理しません。</summary>
    [Fact]
    public void MissingInitializedSqliteIsRejected()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "store");
        SqliteRepository original = new(path);
        Assert.NotNull(original);
        File.Delete(path);
        Assert.Throws<InvalidDataException>(() => new SqliteRepository(path));
    }

    /// <summary>複数インスタンスの競合CASで一つだけを受理します。</summary>
    /// <returns>検証完了。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCompareExchangeHasOneWinner(bool sqlite)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "store");
        IDurableRepository[] stores = Enumerable.Range(0, 8).Select(index => Create(path, sqlite)).ToArray();
        DurableReceipt[] results = await Task.WhenAll(stores.Select((store, index) => Task.Run(async () => await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:counter", "value", null, NodeValue.FromObject(index))]).ConfigureAwait(false))));
        Assert.Single(results, result => result.Outcome == EOutcome.Succeeded);
        Assert.Equal(7, results.Count(result => result.Outcome == EOutcome.Failed));
    }

    /// <summary>ローカル診断リングの更新回数で保存履歴が増大しません。</summary>
    /// <returns>検証完了。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalSnapshotReplacementDoesNotAccumulateHistory(bool sqlite)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "store");
        IDurableRepository store = Create(path, sqlite);
        await store.WriteLocalSnapshotAsync("peer:one:resources", "history", NodeValue.FromObject(0));
        long initialBytes = new FileInfo(path).Length;
        for (int value = 1; value < 100; value++)
        {
            await store.WriteLocalSnapshotAsync("peer:one:resources", "history", NodeValue.FromObject(value));
        }
        Assert.True(new FileInfo(path).Length <= initialBytes * 2);
        Assert.Equal(99, (await Create(path, sqlite).ReadAsync("peer:one:resources", "history"))!.Value!.Json.GetInt32());
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.WriteLocalSnapshotAsync("workflow:shared", "value", NodeValue.Null).ConfigureAwait(false));
    }
}
