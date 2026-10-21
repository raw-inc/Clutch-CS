using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>共有版のキャッシュでも実内容の検証と他媒体との一致を維持します。</summary>
public sealed class SqliteSharedVersionTests
{
    /// <summary>二段階保存の予約だけが変わるとき、大きい現在値を再作成せず既存形式の版を返します。</summary>
    /// <returns>予約・取消・確定と保持開始前後の互換性、実割当量の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedOperationsReuseVerifiedCurrentValues(bool retained)
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
        Guid operation = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflow", "small", null, NodeValue.FromObject(42))];
        Assert.Equal(EOutcome.Succeeded, (await store.PrepareAsync(operation, mutations).ConfigureAwait(true)).Outcome);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        string pending = await store.GetSharedVersionAsync().ConfigureAwait(true);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 262144);
        Assert.Equal(RepositoryStateVersion.Hash(await store.ExportAsync().ConfigureAwait(true)), pending);
        Assert.Equal(retained, !string.Equals(before, pending, StringComparison.Ordinal));
        await store.AbortPreparedAsync(operation).ConfigureAwait(true);
        allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, await store.GetSharedVersionAsync().ConfigureAwait(true));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 262144);
        Assert.Equal(EOutcome.Succeeded, (await store.ApplyBatchAsync(Guid.NewGuid(), mutations).ConfigureAwait(true)).Outcome);
        string committed = await store.GetSharedVersionAsync().ConfigureAwait(true);
        Assert.NotEqual(before, committed);
        Assert.Equal(RepositoryStateVersion.Hash(await store.ExportAsync().ConfigureAwait(true)), committed);
    }

    /// <summary>大きい共有値が変わらない確認で、その値のJSONオブジェクトを再作成しません。</summary>
    /// <returns>canonical ハッシュと有限割当量の検証です。</returns>
    [Fact]
    public async Task UnchangedLargeSharedValueUsesBoundedBuffers()
    {
        using MediaTestFiles files = new();
        SqliteRepository store = new(Path.Combine(files.Root, "state.db"));
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflow", "large", null, NodeValue.FromObject(new string('x', 2097152)))]);
        string version = await store.GetSharedVersionAsync();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(version, await store.GetSharedVersionAsync());
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 262144);
        JsonFileRepository other = new(Path.Combine(files.Root, "state.json"));
        Assert.True(await other.ImportAsync(await store.ExportAsync()));
        Assert.Equal(version, await other.GetSharedVersionAsync());
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflow", "other", null, NodeValue.FromObject(2))]);
        Assert.NotEqual(version, await store.GetSharedVersionAsync());
    }

    /// <summary>cached ハッシュと同じ保存ハッシュでも、バイト列の破損を次の確認で拒否します。</summary>
    /// <returns>実SQLite行の改変に対する検証です。</returns>
    [Fact]
    public async Task CachedVersionStillDetectsChangedPayloadWithUnchangedChecksum()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflow", "a", null, NodeValue.FromObject("値🌐"))]);
        _ = await store.GetSharedVersionAsync();
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE records SET payload=payload || ' ' WHERE kind='entry' AND scope='workflow'";
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetSharedVersionAsync().AsTask()).ConfigureAwait(true);
    }

    /// <summary>UTF-16保存はバイト列の別表現を破損とせず、従来のUTF-8検証を使います。</summary>
    /// <returns>保存encoding互換性です。</returns>
    [Fact]
    public async Task Utf16RepositoryRetainsCanonicalVersionCompatibility()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA encoding='UTF-16le'; CREATE TABLE repository_identity(version INTEGER NOT NULL); INSERT INTO repository_identity VALUES(1);";
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }
        SqliteRepository store = new(path);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflow", "a", null, NodeValue.FromObject("日本語🌐"))]);
        string version = await store.GetSharedVersionAsync();
        Assert.Equal(version, await store.GetSharedVersionAsync());
        JsonFileRepository other = new(Path.Combine(files.Root, "state.json"));
        Assert.True(await other.ImportAsync(await store.ExportAsync()));
        Assert.Equal(version, await other.GetSharedVersionAsync());
    }
}
