using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>直近検索は日数ではなく保存形式で絞り、圧縮本文を開かずにページを返します。</summary>
public sealed class UncompressedLogSearchTests
{
    /// <summary>再起動直後の未索引ファイルも、直近検索だけでは圧縮本文を復元しません。</summary>
    /// <returns>公開検索APIの初回限定検索と、後からの全履歴復元です。</returns>
    [Fact]
    public async Task ColdSearchDefersUnindexedCompressedHistoryUntilRequestedAsync()
    {
        using LogTestEnvironment fixture = new();
        LogArchive archive = new(fixture.Logs);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WorkflowLogEntry entry = new(Guid.NewGuid(), now, "DEBUG", "peer", "unindexed");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        LogSegment raw = new(Guid.NewGuid(), "DEBUG", DateOnly.FromDateTime(now.UtcDateTime), [entry.Id], bytes.Length + 1, false, false, []);
        await archive.WriteAsync(raw, [bytes]).ConfigureAwait(true);
        LogSegment other = raw with { Id = Guid.NewGuid() };
        await archive.WriteAsync(other, [JsonSerializer.SerializeToUtf8Bytes(entry with { Id = Guid.NewGuid(), Message = "compressed" })]).ConfigureAwait(true);
        LogSegment compressed = await archive.CompressAsync(other).ConfigureAwait(true);
        archive.DeleteOldForm(compressed);
        string path = Assert.Single(Directory.GetFiles(fixture.Logs, "*.zst", SearchOption.AllDirectories));
        WorkflowLogService service = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = service.ConfigureAwait(true);
        using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            LogQueryResult recent = await service.QueryAsync(new(UncompressedOnly: true)).ConfigureAwait(true);
            Assert.True(recent.Complete);
            Assert.Equal(entry.Id, Assert.Single(recent.Entries).Id);
        }
        LogQueryResult all = await service.QueryAsync(new()).ConfigureAwait(true);
        Assert.True(all.Complete);
        Assert.Equal(2, all.Entries.Count);
    }

    /// <summary>新しい圧縮ログを含めず、古くても非圧縮で保持するログは全ページに残します。</summary>
    /// <returns>日時順、続き位置、圧縮履歴を含める切替の確認です。</returns>
    [Fact]
    public async Task RawPagesIgnoreCompressedBodiesRegardlessOfEventAgeAsync()
    {
        using LogTestEnvironment fixture = new();
        LogOptions options = new("peer", fixture.Logs, [new("DEBUG", 0, 30), new("INFO", 30, 60)]);
        using LogStore store = new(options, fixture.Repository);
        DateTimeOffset now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        WorkflowLogEntry[] raw = Enumerable.Range(0, 5).Select(index => new WorkflowLogEntry(Guid.NewGuid(), now.AddDays(-7).AddSeconds(index), "INFO", "peer", "raw-" + index)).ToArray();
        WorkflowLogEntry compressed = new(Guid.NewGuid(), now, "DEBUG", "peer", "compressed-newer");
        await PersistAsync(store, options, [.. raw, compressed]).ConfigureAwait(true);
        await store.MaintainAsync(new(options), now, false).ConfigureAwait(true);
        Assert.Single(store.Segments, segment => segment.Compressed);
        string compressedPath = Assert.Single(Directory.GetFiles(fixture.Logs, "*.zst", SearchOption.AllDirectories));
        using (FileStream locked = new(compressedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            List<Guid> actual = [];
            string? cursor = null;
            do
            {
                LogQueryResult page = await LogSearch.QueryAsync(store, new(Limit: 2, Cursor: cursor, UncompressedOnly: true), []).ConfigureAwait(true);
                Assert.True(page.Complete);
                Assert.NotEmpty(page.Entries);
                actual.AddRange(page.Entries.Select(entry => entry.Id));
                cursor = page.Cursor;
            }
            while (cursor is not null && actual.Count < 10);
            Assert.Null(cursor);
            Assert.Equal(raw.Reverse().Select(entry => entry.Id), actual);
            LogQueryResult unreadable = await LogSearch.QueryAsync(store, new(), []).ConfigureAwait(true);
            Assert.False(unreadable.Complete);
        }
        LogQueryResult all = await LogSearch.QueryAsync(store, new(), []).ConfigureAwait(true);
        Assert.True(all.Complete);
        Assert.Equal(6, all.Entries.Count);
        Assert.Equal(compressed.Id, all.Entries[0].Id);
    }

    /// <summary>検索開始後に圧縮が完了した古い索引でも、圧縮ファイルへフォールバックしません。</summary>
    /// <returns>保持処理との競合と、本当の欠損の区別です。</returns>
    [Fact]
    public async Task CompressionDuringSearchDoesNotExpandTheReplacementAsync()
    {
        using LogTestEnvironment fixture = new();
        LogOptions options = new("peer", fixture.Logs);
        using LogStore store = new(options, fixture.Repository);
        WorkflowLogEntry entry = new(Guid.NewGuid(), DateTimeOffset.UtcNow, "DEBUG", "peer", "moving");
        await PersistAsync(store, options, [entry]).ConfigureAwait(true);
        LogSegment raw = Assert.Single(store.Segments);
        LogArchive archive = new(fixture.Logs);
        LogSegment compressed = await archive.CompressAsync(raw).ConfigureAwait(true);
        archive.DeleteOldForm(compressed);
        Assert.False(Assert.Single(store.Segments).Compressed);
        string path = Assert.Single(Directory.GetFiles(fixture.Logs, "*.zst", SearchOption.AllDirectories));
        using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            LogQueryResult recent = await LogSearch.QueryAsync(store, new(UncompressedOnly: true), []).ConfigureAwait(true);
            Assert.True(recent.Complete);
            Assert.Empty(recent.Entries);
            Assert.Null(recent.Cursor);
        }
        LogQueryResult all = await LogSearch.QueryAsync(store, new(), []).ConfigureAwait(true);
        Assert.True(all.Complete);
        Assert.Equal(entry.Id, Assert.Single(all.Entries).Id);
        File.Delete(path);
        LogQueryResult missing = await LogSearch.QueryAsync(store, new(UncompressedOnly: true), []).ConfigureAwait(true);
        Assert.False(missing.Complete);
    }

    /// <summary>従来のAPI要求は圧縮履歴を含むままとし、限定条件は明示時だけ送信します。</summary>
    [Fact]
    public void ExistingQueryWireShapeAndDefaultArePreserved()
    {
        LogQuery old = JsonSerializer.Deserialize<LogQuery>("{}", ContractJson.Options)!;
        Assert.False(old.UncompressedOnly);
        Assert.False(ContractJson.ToValue(old).Json.TryGetProperty("uncompressedOnly", out _));
        LogQuery recent = old with { UncompressedOnly = true };
        Assert.True(ContractJson.ToValue(recent).Json.GetProperty("uncompressedOnly").GetBoolean());
        Assert.True(ContractJson.ToValue(recent).Json.Deserialize<LogQuery>(ContractJson.Options)!.UncompressedOnly);
    }

    /// <summary>保持日数に依存せず、一回の保存済みログを作ります。</summary>
    /// <returns>実本文と索引の保存完了です。</returns>
    private static Task PersistAsync(LogStore store, LogOptions options, IReadOnlyList<WorkflowLogEntry> entries)
        => store.PersistAsync(entries.Select(entry => new LogWorkItem(JsonSerializer.SerializeToUtf8Bytes(entry), false)).ToArray(), new(options));
}
