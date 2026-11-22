using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>実行日時を保持したまま、活動の順序・窓・再読込みを単調時計で確認します。</summary>
public sealed class ObservedExecutionHistoryTests
{
    /// <summary>時計を一時間戻しても、最後に完了した結果を保持し、古いカーソルへ新着を混入させません。</summary>
    [Fact]
    public void CompletionOrderAndEvictionIgnoreUtcCorrection()
    {
        ResourceSelectionClock clock = new();
        ExecutionReportCache cache = new(maximumCount: 2, timeProvider: clock);
        Guid instance = Guid.NewGuid();
        ExecutionReport first = Report(clock), second = Report(clock);
        cache.Remember(first);
        clock.Advance(TimeSpan.FromSeconds(1));
        cache.Remember(second);
        ExecutionHistoryPage page = ObservedExecutionHistory.Page(cache.RecentViews(), new(1, SummariesOnly: true), instance);
        Assert.Equal(second.Id, Assert.Single(page.Entries).Summary!.Id);
        Assert.NotNull(page.Cursor);
        clock.Adjust(TimeSpan.FromHours(-1));
        ExecutionReport third = Report(clock);
        cache.Remember(third);
        Assert.Null(cache.Find(first.Id));
        Assert.NotNull(cache.Find(second.Id));
        Assert.Empty(ObservedExecutionHistory.Page(cache.RecentViews(), new(1, page.Cursor, SummariesOnly: true), instance).Entries);
        ExecutionResultView latest = Assert.Single(ObservedExecutionHistory.Page(cache.RecentViews(), new(1, SummariesOnly: true), instance).Entries);
        Assert.Equal(third.Id, latest.Summary!.Id);
        Assert.Equal(third.CompletedAt, latest.Summary.CompletedAt);
        Assert.Throws<ArgumentException>(() => ObservedExecutionHistory.Page(cache.RecentViews(), new(1, page.Cursor), Guid.NewGuid()));
    }

    /// <summary>再保存やUTC補正で同じ実行の年齢を若返らせず、60秒と5分の用途別上限を守ります。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void RepeatedReportsKeepTheirOriginalLifetime(int correctionSeconds)
    {
        ResourceSelectionClock clock = new();
        ExecutionReportCache cache = new(timeProvider: clock);
        ExecutionReport report = Report(clock);
        cache.Remember(report);
        clock.Advance(TimeSpan.FromSeconds(59));
        clock.Adjust(TimeSpan.FromSeconds(correctionSeconds));
        cache.Remember(report);
        Guid instance = Guid.NewGuid();
        ExecutionHistoryPage first = ObservedExecutionHistory.Page(cache.RecentViews(), new(SummariesOnly: true), instance);
        Assert.Equal(TimeSpan.FromSeconds(59).Ticks, Assert.Single(first.Entries).CompletionAgeTicks);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(ObservedExecutionHistory.Page(cache.RecentViews(), new(SummariesOnly: true), instance).Entries);
        Assert.Single(ObservedExecutionHistory.Page(cache.RecentViews(), new(), instance).Entries);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(ObservedExecutionHistory.Page(cache.RecentViews(), new(), instance).Entries);
        Assert.Equal(report, Assert.Single(ExecutionHistory.Page(cache.RecentViews(), new()).Entries).Report);
    }

    /// <summary>再起動後の履歴を新着として数えず、読み込んでから窓の時間が経てば窓外だと確定できます。</summary>
    [Fact]
    public void RestoredHistoryIsUnconfirmedUntilItsMinimumAgeLeavesTheWindow()
    {
        ResourceSelectionClock clock = new();
        ExecutionResultView restored = new(Report(clock)) { CompletionAge = new(clock), CompletionAgeIsLowerBound = true };
        Guid instance = Guid.NewGuid();
        ExecutionHistoryPage first = ObservedExecutionHistory.Page([restored], new(SummariesOnly: true), instance);
        Assert.Empty(first.Entries);
        Assert.True(first.AgeUnconfirmed);
        clock.Adjust(TimeSpan.FromDays(1));
        Assert.True(ObservedExecutionHistory.Page([restored], new(SummariesOnly: true), instance).AgeUnconfirmed);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(ObservedExecutionHistory.Page([restored], new(SummariesOnly: true), instance).AgeUnconfirmed);
        Assert.True(ObservedExecutionHistory.Page([restored], new(), instance).AgeUnconfirmed);
        Assert.Equal(restored.Report, Assert.Single(ExecutionHistory.Page([restored], new()).Entries).Report);
    }

    /// <summary>同時刻の大量完了も有限のページで全件辿れ、経過時間を永続履歴へ混ぜません。</summary>
    [Fact]
    public void BoundedPagesPreserveEveryCompletionAndKeepPersistenceMetadataFree()
    {
        ResourceSelectionClock clock = new();
        ExecutionReportCache cache = new(timeProvider: clock);
        ExecutionReport[] reports = Enumerable.Range(0, 205).Select(_ => Report(clock)).ToArray();
        foreach (ExecutionReport report in reports)
        {
            cache.Remember(report);
        }
        Guid instance = Guid.NewGuid();
        List<Guid> received = [];
        string? cursor = null;
        do
        {
            ExecutionHistoryPage page = ObservedExecutionHistory.Page(cache.RecentViews(), new(100, cursor, 16384, true), instance);
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(page, ContractJson.Options).Length, 1, 16384);
            received.AddRange(page.Entries.Select(view => view.Summary!.Id));
            cursor = page.Cursor;
        }
        while (cursor is not null);
        Assert.Equal(reports.Select(report => report.Id).Reverse(), received);
        ExecutionResultView view = cache.RecentViews()[0];
        string expected = ContractJson.Hash(new ExecutionResultView(view.Report, view.Summary, view.DetailReason));
        Assert.Equal(expected, ContractJson.Hash(view));
    }

    /// <summary>小さな完了記録を現在の表示日時で作ります。</summary>
    /// <returns>再実行しない診断専用の結果です。</returns>
    private static ExecutionReport Report(TimeProvider clock) => new(Guid.NewGuid(), "workflow", "revision", EOutcome.Succeeded, clock.GetUtcNow(), clock.GetUtcNow(), []);
}
