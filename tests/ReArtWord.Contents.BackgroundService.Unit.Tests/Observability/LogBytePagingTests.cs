using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>巨大な一件でも検索ページの続きを失わないことを確認します。</summary>
public sealed class LogBytePagingTests
{
    /// <summary>巨大データとメッセージを明示省略し、バイトで区切った全件を再読できます。</summary>
    /// <returns>実ファイル検索の検証の完了。</returns>
    [Fact]
    public async Task LargeEntryDoesNotBlockFollowingPages()
    {
        using LogTestEnvironment fixture = new();
        LogTestClock clock = new();
        WorkflowLogService log = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets, clock);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        DateTimeOffset timestamp = clock.GetUtcNow();
        Guid huge = Guid.NewGuid();
        await log.WriteAsync(new(huge, timestamp, "DEBUG", "peer", new string('m', 100000),
            Data: NodeValue.FromObject(new string('d', 200000))), timeout.Token).ConfigureAwait(true);
        for (int index = 1; index <= 12; index++)
        {
            await log.WriteAsync(new(Guid.NewGuid(), timestamp.AddSeconds(index), "DEBUG", "peer", new string('s', 5000)), timeout.Token).ConfigureAwait(true);
        }
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        List<Guid> ids = [];
        List<LogEntryOmission> omitted = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            LogQueryResult page = await log.QueryAsync(new(Limit: 100, Cursor: cursor, MaximumBytes: 16384), timeout.Token).ConfigureAwait(true);
            Assert.True(page.Complete);
            Assert.NotEmpty(page.Entries);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, ContractJson.Options).Length <= 16384);
            ids.AddRange(page.Entries.Select(static item => item.Id));
            omitted.AddRange(page.Omissions ?? []);
            cursor = page.Cursor;
            pages++;
        }
        while (cursor is not null && pages < 20);
        Assert.Null(cursor);
        Assert.True(1 < pages);
        Assert.Equal(13, ids.Distinct().Count());
        LogEntryOmission large = Assert.Single(omitted, item => item.EntryId == huge);
        Assert.Contains("data", large.Fields);
        Assert.Contains("message", large.Fields);
    }
}
