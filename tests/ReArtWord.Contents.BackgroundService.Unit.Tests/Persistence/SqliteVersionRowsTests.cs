using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>小さい変更で未変更の大きい値を展開せず、既存の共有版と破損検査を維持します。</summary>
public sealed class SqliteVersionRowsTests
{
    /// <summary>別行の変更で大きい現在値を再作成せず、予約・取消・確定後も従来の版と一致します。</summary>
    /// <returns>実割当量の上限と、別のJSON保存実装との内容互換性です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmallUpdatesReuseUnchangedLargeRows(bool retained)
    {
        using MediaTestFiles files = new();
        SqliteRepository store = new(Path.Combine(files.Root, "state.db"));
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflow", "large", null, NodeValue.FromObject(new string('x', 2097152)))]).ConfigureAwait(true);
        if (retained)
        {
            using HistoryArchiveService archive = new(store);
            await archive.MaintainAsync(new(), new(1, 3), DateTimeOffset.UtcNow.AddDays(8)).ConfigureAwait(true);
        }
        string before = await store.GetSharedVersionAsync().ConfigureAwait(true);
        string? revision = null;
        for (int index = 0; index < 3; index++)
        {
            Guid id = Guid.NewGuid();
            Assert.Equal(EOutcome.Succeeded, (await store.PrepareAsync(id, [new("workflow", "small", revision, NodeValue.FromObject(index))]).ConfigureAwait(true)).Outcome);
            _ = await store.GetSharedVersionAsync().ConfigureAwait(true);
            revision = (await store.CommitAsync(id).ConfigureAwait(true)).Entries.Single().Version;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            string changed = await store.GetSharedVersionAsync().ConfigureAwait(true);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 1048576);
            Assert.NotEqual(before, changed);
            JsonFileRepository other = new(Path.Combine(files.Root, "other-" + index + ".json"));
            Assert.True(await other.ImportAsync(await store.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
            Assert.Equal(await other.GetSharedVersionAsync().ConfigureAwait(true), changed);
            before = changed;
        }
    }

    /// <summary>同じDBへの別所有者の更新・削除と、異なる分岐の取込みも行の再利用から漏れません。</summary>
    /// <returns>操作後の実内容の変化と、現在値・競合候補を含む従来形式との一致です。</returns>
    [Fact]
    public async Task ExternalUpdatesDeletionAndConflictsInvalidateTheirRows()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path), other = new(path);
        DurableEntry first = (await store.ApplyBatchAsync(Guid.NewGuid(), [new("state", "item", null, NodeValue.FromObject(new { text = "日本語🌐", value = 1 }))]).ConfigureAwait(true)).Entries.Single();
        RepositorySnapshot original = await store.ExportAsync().ConfigureAwait(true);
        string before = await store.GetSharedVersionAsync().ConfigureAwait(true);
        DurableEntry updated = (await other.ApplyBatchAsync(Guid.NewGuid(), [new("state", "item", first.Version, NodeValue.FromObject(new { value = 2, text = "日本語🌐" }))]).ConfigureAwait(true)).Entries.Single();
        Assert.NotEqual(before, await store.GetSharedVersionAsync().ConfigureAwait(true));
        Assert.Equal(RepositoryStateVersion.Hash(await store.ExportAsync().ConfigureAwait(true)), await store.GetSharedVersionAsync().ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await other.ApplyBatchAsync(Guid.NewGuid(), [new("state", "item", updated.Version, null)]).ConfigureAwait(true)).Outcome);
        string deleted = await store.GetSharedVersionAsync().ConfigureAwait(true);
        Assert.Equal(RepositoryStateVersion.Hash(await store.ExportAsync().ConfigureAwait(true)), deleted);
        JsonFileRepository branch = new(Path.Combine(files.Root, "branch.json"));
        Assert.True(await branch.ImportAsync(original).ConfigureAwait(true));
        await branch.ApplyBatchAsync(Guid.NewGuid(), [new("state", "item", first.Version, NodeValue.FromObject("other branch"))]).ConfigureAwait(true);
        Assert.False(await store.ImportAsync(await branch.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
        Assert.NotEqual(deleted, await store.GetSharedVersionAsync().ConfigureAwait(true));
        Assert.Equal(RepositoryStateVersion.Hash(await store.ExportAsync().ConfigureAwait(true)), await store.GetSharedVersionAsync().ConfigureAwait(true));
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM records WHERE kind IN ('entry','conflict') AND scope='state'";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.Empty(await store.ListAsync("state").ConfigureAwait(true));
        Assert.Equal(RepositoryStateVersion.Hash(await store.ExportAsync().ConfigureAwait(true)), await store.GetSharedVersionAsync().ConfigureAwait(true));
    }

    /// <summary>本文が変わらなくても失効索引の追加・削除を共有版へ反映します。</summary>
    /// <returns>同じ保存ハッシュを持つ行の保持境界変更です。</returns>
    [Fact]
    public async Task RetirementChangesReevaluateUnchangedRows()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        Dictionary<ERetentionCategory, DateTimeOffset> empty = [];
        HistoryRetentionBoundary boundary = new(empty, empty, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        Assert.True(await store.ImportAsync(new([], [], HistoryBoundary: boundary)).ConfigureAwait(true));
        string scope = "execution:scheduled-inputs", key = Guid.NewGuid().ToString("N");
        await store.ApplyBatchAsync(Guid.NewGuid(), [new(scope, key, null, NodeValue.FromObject(42))]).ConfigureAwait(true);
        string before = await store.GetSharedVersionAsync().ConfigureAwait(true);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO retired_keys(scope,key) VALUES($scope,$key)";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        string retired = await store.GetSharedVersionAsync().ConfigureAwait(true);
        Assert.NotEqual(before, retired);
        Assert.Equal(RepositoryStateVersion.Hash(await store.ExportAsync().ConfigureAwait(true)), retired);
        command.CommandText = "DELETE FROM retired_keys WHERE scope=$scope AND key=$key";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.Equal(before, await store.GetSharedVersionAsync().ConfigureAwait(true));
    }
}
