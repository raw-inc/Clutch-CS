using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>活動表示の完了順と、通常履歴の開始順を分けて確認します。</summary>
public sealed class ExecutionActivityTests
{
    /// <summary>長時間実行の完了を先頭へ戻し、大きい出力で他の活動を押し出しません。</summary>
    [Fact]
    public void LateCompletionIsFirstWithoutTransferringOutputs()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExecutionReport late = new(Guid.NewGuid(), "long-running", "revision", EOutcome.Failed, now.AddHours(-1), now,
            [new("node", EOutcome.Failed, [new("peer", NodeResult.Success(NodeValue.FromObject(new string('x', 30000))))])]);
        ExecutionResultView recent = new(new(Guid.NewGuid(), "short-running", "revision", EOutcome.Succeeded, now.AddSeconds(-2), now.AddSeconds(-1), []));
        ExecutionResultView[] reports = [recent, new(late)];
        ExecutionHistoryPage page = ExecutionHistory.ActivityPage(reports, new(100, MaximumBytes: 16384));
        Assert.Equal(new[] { late.Id, recent.Report!.Id }, page.Entries.Select(entry => entry.Summary!.Id));
        Assert.Equal(EOutcome.Failed, page.Entries[0].Summary!.Outcome);
        Assert.All(page.Entries, entry =>
        {
            Assert.Null(entry.Report);
            Assert.Empty(entry.Summary!.Targets);
        });
        Assert.Null(page.Cursor);
        ExecutionHistoryPage normal = ExecutionHistory.Page(reports, new(1));
        Assert.Equal(recent.Report.Id, Assert.Single(normal.Entries).Report!.Id);
    }

    /// <summary>同時刻の完了も件数・バイト制限を守り、全ページを重複なく取得できます。</summary>
    [Fact]
    public void CompletionPagesKeepStableBoundariesAcrossNewArrivals()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<ExecutionResultView> reports = Enumerable.Range(0, 205).Select(index => new ExecutionResultView(
            new(Guid.NewGuid(), "workflow-" + index, "revision", EOutcome.Succeeded, now.AddSeconds(-index), now, []))).ToList();
        Guid[] expected = reports.Select(entry => entry.Report!.Id).Order().ToArray();
        List<Guid> received = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            ExecutionHistoryPage page = ExecutionHistory.ActivityPage(reports, new(100, cursor, 16384));
            Assert.NotEmpty(page.Entries);
            Assert.InRange(page.Entries.Count, 1, 100);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, ContractJson.Options).Length <= 16384);
            received.AddRange(page.Entries.Select(entry => entry.Summary!.Id));
            cursor = page.Cursor;
            reports.Add(new(new(Guid.NewGuid(), "new-arrival", "revision", EOutcome.Succeeded, now.AddHours(-2), now.AddSeconds(1), [])));
            pages++;
        }
        while (cursor is not null && pages < 20);
        Assert.Null(cursor);
        Assert.True(2 < pages);
        Assert.Equal(expected, received.Order());
    }
}
