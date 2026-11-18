using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>操作IDの変更が、ログの日付による保持と時刻・IDによるページングを変えないことを確認します。</summary>
public sealed class LogIdentityRetentionTests
{
    /// <summary>旧日時境界が未来でも、期限を過ぎたログ本文と索引を一緒に削除できます。</summary>
    /// <returns>実ファイルと二つの媒体での保持整理です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOperationBoundaryDoesNotBlockExpiredLogDeletionAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = json ? new JsonFileRepository(Path.Combine(files.Root, "state.json"))
                                             : new SqliteRepository(Path.Combine(files.Root, "state.db"));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HistoryRetentionBoundary boundary = new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(), now.AddDays(1), now.AddDays(31));
        await repository.WriteLocalSnapshotAsync(RepositoryHistoryGuard.Scope, "boundary", NodeValue.FromObject(boundary)).ConfigureAwait(true);
        LogOptions options = new("peer", Path.Combine(files.Root, "logs"), [new("INFO", 0, 1)], TimeZoneId: "UTC");
        using (LogStore store = new(options, repository))
        {
            WorkflowLogEntry entry = new(Guid.CreateVersion7(now), now.AddDays(-3), "INFO", "peer", "expired by event date");
            await store.PersistAsync([new(JsonSerializer.SerializeToUtf8Bytes(entry), false)], new(options)).ConfigureAwait(true);
            Assert.Single(store.Segments);
            await store.MaintainAsync(new(options), now, false).ConfigureAwait(true);
            Assert.Empty(store.Segments);
            Assert.Null(Assert.Single(await repository.ListAsync("peer:peer:log-index").ConfigureAwait(true)).Value);
        }
        using LogStore reopened = new(options, repository);
        await reopened.InitializeAsync().ConfigureAwait(true);
        Assert.Empty(reopened.Segments);
        Assert.Empty(Directory.EnumerateFiles(options.Directory, "*", SearchOption.AllDirectories));
    }

    /// <summary>同じ記録日時に旧・新UUIDが混在しても、全ページを重複・欠落なく返します。</summary>
    /// <returns>UUIDの日時ではなく、ログの記録日時と元IDを使う検索です。</returns>
    [Fact]
    public async Task MixedUuidVersionsKeepStablePagesAtTheSameLogTimeAsync()
    {
        using LogTestEnvironment files = new();
        LogOptions options = new("peer", files.Logs);
        using LogStore store = new(options, files.Repository);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OperationIdSequence sequence = new();
        Guid[] ids = [Guid.NewGuid(), Guid.CreateVersion7(now.AddYears(-1)), sequence.Next(), Guid.CreateVersion7(now.AddDays(31)), sequence.Next()];
        WorkflowLogEntry[] entries = ids.Select(id => new WorkflowLogEntry(id, now, "INFO", "peer", "same event time")).ToArray();
        await store.PersistAsync(entries.Select(entry => new LogWorkItem(JsonSerializer.SerializeToUtf8Bytes(entry), false)).ToArray(), new(options)).ConfigureAwait(true);
        List<Guid> actual = [];
        string? cursor = null;
        do
        {
            LogQueryResult page = await LogSearch.QueryAsync(store, new(Limit: 2, Cursor: cursor), []).ConfigureAwait(true);
            Assert.True(page.Complete);
            actual.AddRange(page.Entries.Select(entry => entry.Id));
            cursor = page.Cursor;
        }
        while (cursor is not null && actual.Count <= ids.Length);
        Assert.Null(cursor);
        Assert.Equal(ids.OrderDescending(), actual);
    }
}
