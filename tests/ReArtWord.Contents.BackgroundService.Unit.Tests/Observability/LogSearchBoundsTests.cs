using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>時刻索引で走査を省いても、遅着・同時刻・旧索引を検索から落としません。</summary>
public sealed class LogSearchBoundsTests
{
    /// <summary>保存順とイベント時刻が異なっても、範囲と続き位置を保ちます。</summary>
    /// <param name="compressed">圧縮した本文からも同じ結果を読みます。</param>
    /// <returns>全ページに重複も欠落もないことの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateAndEqualTimestampsKeepEveryPageAsync(bool compressed)
    {
        using LogTestEnvironment fixture = new();
        LogOptions options = new("peer", fixture.Logs, [new("DEBUG", 0, 30)]);
        using LogStore store = new(options, fixture.Repository);
        DateTimeOffset origin = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        WorkflowLogEntry[] entries = Enumerable.Range(0, 18).Select(index => new WorkflowLogEntry(Guid.NewGuid(),
            origin.AddSeconds(index % 6), "DEBUG", "peer", "entry-" + index)).ToArray();
        foreach (WorkflowLogEntry[] batch in entries.Chunk(3).Reverse())
        {
            await PersistAsync(store, options, batch).ConfigureAwait(true);
        }
        if (compressed)
        {
            await store.MaintainAsync(new(options), origin.AddHours(1), false).ConfigureAwait(true);
            Assert.All(store.Segments, segment => Assert.True(segment.Compressed));
        }
        List<Guid> actual = [];
        string? cursor = null;
        do
        {
            LogQueryResult result = await LogSearch.QueryAsync(store, new(From: origin.AddSeconds(1), Until: origin.AddSeconds(4),
                Limit: 2, Cursor: cursor), []).ConfigureAwait(true);
            Assert.True(result.Complete);
            Assert.NotEmpty(result.Entries);
            actual.AddRange(result.Entries.Select(entry => entry.Id));
            cursor = result.Cursor;
        }
        while (cursor is not null && actual.Count < 30);
        Guid[] expected = entries.Where(entry => origin.AddSeconds(1) <= entry.Time && entry.Time <= origin.AddSeconds(4))
            .OrderByDescending(entry => entry.Time).ThenByDescending(entry => entry.Id).Select(entry => entry.Id).ToArray();
        Assert.Equal(expected, actual);
        Assert.Null(cursor);
    }

    /// <summary>対象外の本文は読まず、対象の欠損は不完全な検索結果にします。</summary>
    /// <returns>索引による省略と障害範囲の確認です。</returns>
    [Fact]
    public async Task MissingOldFileDoesNotInvalidateAnUnrelatedPageAsync()
    {
        using LogTestEnvironment fixture = new();
        LogOptions options = new("peer", fixture.Logs);
        using LogStore store = new(options, fixture.Repository);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await PersistAsync(store, options, [new(Guid.NewGuid(), now.AddDays(-1), "DEBUG", "peer", "old")]).ConfigureAwait(true);
        string old = Assert.Single(Directory.GetFiles(fixture.Logs, "*.jsonl", SearchOption.AllDirectories));
        await PersistAsync(store, options, [new(Guid.NewGuid(), now, "DEBUG", "peer", "new-1"),
            new(Guid.NewGuid(), now, "DEBUG", "peer", "new-2")]).ConfigureAwait(true);
        File.Delete(old);
        LogQueryResult latest = await LogSearch.QueryAsync(store, new(Limit: 1), []).ConfigureAwait(true);
        Assert.True(latest.Complete);
        Assert.StartsWith("new-", Assert.Single(latest.Entries).Message, StringComparison.Ordinal);
        LogQueryResult missing = await LogSearch.QueryAsync(store, new(Until: now.AddHours(-1)), []).ConfigureAwait(true);
        Assert.False(missing.Complete);
        Assert.Empty(missing.Entries);
    }

    /// <summary>旧索引は本文を最後まで読んだときだけ補完し、途中終了で誤った範囲を確定しません。</summary>
    /// <returns>長い行を含む既存ログを、欠落なく一度だけ索引化できることの確認です。</returns>
    [Fact]
    public async Task LegacyBoundsAreLearnedOnlyAfterACompleteReadAsync()
    {
        using LogTestEnvironment fixture = new();
        LogOptions options = new("peer", fixture.Logs);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LogSegment legacy;
        using (LogStore writer = new(options, fixture.Repository))
        {
            await PersistAsync(writer, options, [new(Guid.NewGuid(), now, "DEBUG", "peer", new string('x', 150000)),
                new(Guid.NewGuid(), now.AddSeconds(1), "DEBUG", "peer", "last")]).ConfigureAwait(true);
            legacy = Assert.Single(writer.Segments) with { FirstTime = null, LastTime = null };
            _ = await fixture.Repository.WriteLocalSnapshotAsync("peer:peer:log-index", legacy.Id.ToString("N"), NodeValue.FromObject(legacy)).ConfigureAwait(true);
        }
        using LogStore reader = new(options, fixture.Repository);
        await reader.InitializeAsync().ConfigureAwait(true);
        Assert.Null(Assert.Single(reader.Segments).FirstTime);
        await foreach (WorkflowLogEntry entry in reader.ReadAsync(legacy).ConfigureAwait(true))
        {
            Assert.Equal(150000, entry.Message.Length);
            break;
        }
        Assert.Null(Assert.Single(reader.Segments).FirstTime);
        LogQueryResult result = await LogSearch.QueryAsync(reader, new(), []).ConfigureAwait(true);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(now, Assert.Single(reader.Segments).FirstTime);
        Assert.Equal(now.AddSeconds(1), Assert.Single(reader.Segments).LastTime);
    }

    /// <summary>ワーカーの時刻に依存せず、一つの不変ログファイルを作ります。</summary>
    /// <returns>実ファイルと索引の保存完了です。</returns>
    private static Task PersistAsync(LogStore store, LogOptions options, IReadOnlyList<WorkflowLogEntry> entries)
        => store.PersistAsync(entries.Select(entry => new LogWorkItem(JsonSerializer.SerializeToUtf8Bytes(entry), false)).ToArray(), new(options));
}
