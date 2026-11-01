using System.Globalization;
using ReArtWord.Contents.BackgroundService.Client.Pages.Logs;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ページを戻る際の条件維持、保持量、取消後の再取得を確認します。</summary>
public sealed class LogPageHistoryTests
{
    /// <summary>近いページは再取得せず、保持範囲より前も同じ条件で欠落なく読み直します。</summary>
    /// <returns>複数取得先をまたぐ往復と、最後のページの判定です。</returns>
    [Fact]
    public async Task RoundTripBeyondCacheKeepsFiltersAndSourceOrderAsync()
    {
        DateTimeOffset until = DateTimeOffset.UnixEpoch.AddDays(1);
        int reads = 0;
        LogSearchSource a = new("a", "peer"), b = new("b", "peer");
        LogQuery expected = new(Until: until, Text: "needle", Limit: 2, UncompressedOnly: true);
        LogPageHistory history = new(expected, [a, b], (source, query, _) =>
        {
            Interlocked.Increment(ref reads);
            Assert.Equal(expected.Until, query.Until);
            Assert.Equal(expected.Text, query.Text);
            Assert.True(query.UncompressedOnly);
            int offset = int.Parse(query.Cursor ?? "0", CultureInfo.InvariantCulture);
            int parity = source == a ? 0
                                     : 1;
            WorkflowLogEntry[] entries = Enumerable.Range(offset, 2).Select(index => Entry(index * 2 + parity)).ToArray();
            string? cursor = offset + 2 < 20 ? (offset + 2).ToString(CultureInfo.InvariantCulture)
                                             : null;
            return Task.FromResult<LogQueryResult?>(new(entries, cursor, true));
        });
        for (int index = 0; index < 20; index++)
        {
            LogSearchPage page = await history.ReadAsync(index).ConfigureAwait(true);
            Assert.Equal([Entry(index * 2).Id, Entry(index * 2 + 1).Id], page.Entries.Select(row => row.Entry.Id));
            Assert.Equal([a, b], page.Entries.Select(row => row.Source));
            Assert.Equal(index != 19, page.HasMore);
        }
        int before = reads;
        await history.ReadAsync(18).ConfigureAwait(true);
        await history.ReadAsync(19).ConfigureAwait(true);
        Assert.Equal(before, reads);
        LogSearchPage first = await history.ReadAsync(0).ConfigureAwait(true);
        Assert.True(before < reads);
        Assert.Equal(0, history.PageIndex);
        Assert.Equal([Entry(0).Id, Entry(1).Id], first.Entries.Select(row => row.Entry.Id));
        LogSearchPage second = await history.ReadAsync(1).ConfigureAwait(true);
        Assert.Equal([Entry(2).Id, Entry(3).Id], second.Entries.Select(row => row.Entry.Id));
    }

    /// <summary>本文が大きい場合もページ単位の容量を守り、残りを次ページへ渡します。</summary>
    /// <returns>一ページの本文上限と、全件取得時の欠落・重複がないことです。</returns>
    [Fact]
    public async Task LargeDetailsStayWithinThePageBudgetAsync()
    {
        WorkflowLogEntry[] entries = Enumerable.Range(0, 60).Select(index => Entry(index) with { Message = new string('x', 30000) }).ToArray();
        LogPageHistory history = new(new(), [new("cluster", "peer")], (_, query, _) =>
        {
            int offset = int.Parse(query.Cursor ?? "0", CultureInfo.InvariantCulture);
            string? cursor = offset + 2 < entries.Length ? (offset + 2).ToString(CultureInfo.InvariantCulture)
                                                         : null;
            return Task.FromResult<LogQueryResult?>(new(entries.Skip(offset).Take(2).ToArray(), cursor, true));
        });
        List<Guid> observed = [];
        for (int index = 0; index < entries.Length; index++)
        {
            LogSearchPage page = await history.ReadAsync(index).ConfigureAwait(true);
            Assert.InRange(page.Entries.Sum(row => JsonSize.Measure(row.Entry)), 1, 524288);
            observed.AddRange(page.Entries.Select(row => row.Entry.Id));
            if (!page.HasMore)
            {
                break;
            }
        }
        Assert.Equal(entries.Select(entry => entry.Id), observed);
        Assert.Equal(entries[0].Id, (await history.ReadAsync(0).ConfigureAwait(true)).Entries[0].Entry.Id);
    }

    /// <summary>ページの途中で取り消しても、再試行時に読取り済みの一部を失いません。</summary>
    /// <returns>表示位置の維持と、次ページを最初から読み直した結果です。</returns>
    [Fact]
    public async Task CancellationKeepsCurrentPageAndRetryDoesNotSkipAsync()
    {
        bool blocking = true;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LogPageHistory history = new(new(Limit: 2), [new("cluster", "peer")], async (_, query, token) =>
        {
            int offset = int.Parse(query.Cursor ?? "0", CultureInfo.InvariantCulture);
            if (offset == 3 && blocking)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }
            return new([Entry(offset)], (offset + 1).ToString(CultureInfo.InvariantCulture), true);
        });
        LogSearchPage first = await history.ReadAsync(0).ConfigureAwait(true);
        using CancellationTokenSource cancellation = new();
        Task<LogSearchPage> pending = history.ReadAsync(1, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await cancellation.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending).ConfigureAwait(true);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        Assert.Equal(0, history.PageIndex);
        Assert.Same(first, await history.ReadAsync(0).ConfigureAwait(true));
        blocking = false;
        LogSearchPage second = await history.ReadAsync(1).ConfigureAwait(true);
        Assert.Equal([Entry(2).Id, Entry(3).Id], second.Entries.Select(row => row.Entry.Id));
        Assert.Equal(1, history.PageIndex);
    }

    /// <summary>新しい順に並ぶ、識別可能なログを生成します。</summary>
    /// <returns>位置から同じIDを再生成できるログです。</returns>
    internal static WorkflowLogEntry Entry(int index)
        => new(new Guid(index + 1, 0, 0, new byte[8]), DateTimeOffset.UnixEpoch.AddSeconds(-index), "INFO", "peer", "entry-" + index.ToString("D3", CultureInfo.InvariantCulture));
}
