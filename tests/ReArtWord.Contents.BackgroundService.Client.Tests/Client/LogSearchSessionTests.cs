using ReArtWord.Contents.BackgroundService.Client.Pages.Logs;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>取得先ごとのページを混同せず、転送済みの重複と部分障害を扱う検索を検証します。</summary>
public sealed class LogSearchSessionTests
{
    /// <summary>大きい詳細のある検索は、件数上限に達する前でも一ページの保持量を制限します。</summary>
    /// <returns>容量境界で保留した候補を次ページへ欠落なく渡す確認です。</returns>
    [Fact]
    public async Task ResultByteBudgetPreservesEveryRecordAsync()
    {
        WorkflowLogEntry[] entries = Enumerable.Range(0, 50).Select(index => new WorkflowLogEntry(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddSeconds(-index),
            "INFO", "peer", new string('x', 8000) + index)).ToArray();
        LogSearchSession search = new(new(Limit: 1000), [new("cluster", "peer")], (_, _, _) => Task.FromResult<LogQueryResult?>(new(entries, null, true)), maximumResultBytes: 20000);
        List<Guid> ids = [];
        LogSearchPage page;
        do
        {
            page = await search.NextAsync();
            Assert.InRange(page.Entries.Count, 1, 2);
            ids.AddRange(page.Entries.Select(row => row.Entry.Id));
        }
        while (page.HasMore);
        Assert.Equal(entries.Select(entry => entry.Id), ids);
        Assert.True(page.Complete);
    }

    /// <summary>Peer別のカーソルを進めても、非圧縮限定の検索条件を保持します。</summary>
    /// <returns>全取得先・全ページへの条件の伝播です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompressionScopeSurvivesEveryPeerPageAsync(bool uncompressedOnly)
    {
        int calls = 0;
        LogSearchSession session = new(new(Limit: 1, UncompressedOnly: uncompressedOnly), [new("a", "peer"), new("b", "peer")], (_, query, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(uncompressedOnly, query.UncompressedOnly);
            if (query.Cursor is null)
            {
                return Task.FromResult<LogQueryResult?>(new([Entry(2)], "next", true));
            }
            return Task.FromResult<LogQueryResult?>(new([Entry(1)], null, true));
        });
        List<ScopedLogEntry> entries = [];
        LogSearchPage page;
        do
        {
            page = await session.NextAsync().ConfigureAwait(true);
            entries.AddRange(page.Entries);
        }
        while (page.HasMore && entries.Count < 10);
        Assert.Equal(4, calls);
        Assert.Equal(4, entries.Count);
        Assert.False(page.HasMore);
    }

    /// <summary>同じクラスタの重複をページ境界でも除き、別クラスタの同じIDは保持します。</summary>
    /// <returns>固定された各カーソルを使う日時順の検索です。</returns>
    [Fact]
    public async Task PagesMergeAllPeersAndRetainClusterIdentityAsync()
    {
        LogSearchSource a = new("a", "one"), b = new("a", "two"), c = new("b", "one");
        LogEntryOmission omission = new(Entry(5).Id, ["data"], "log.pageLimit");
        LogSearchSession session = new(new(Limit: 2), [a, b, c], (source, query, _) => Task.FromResult<LogQueryResult?>((source, query.Cursor) switch
        {
            (var s, null) when s == a => new([Entry(5), Entry(3)], "one/2", true, Omissions: [omission]),
            (var s, "one/2") when s == a => new([Entry(2)], null, true),
            (var s, null) when s == b => new([Entry(5), Entry(4)], "two/2", true),
            (var s, "two/2") when s == b => new([Entry(3), Entry(1)], null, true),
            (var s, null) when s == c => new([Entry(5)], null, true),
            _ => throw new InvalidOperationException("The cursor was sent to the wrong source.")
        }));
        LogSearchPage first = await session.NextAsync().ConfigureAwait(true);
        Assert.Equal(["a:5", "b:5"], first.Entries.Select(row => row.Source.ClusterId + ":" + row.Entry.Message));
        Assert.Equal(omission, first.Entries[0].Omission);
        Assert.True(first.HasMore);
        Assert.True(first.Complete);
        Assert.Equal(3, first.RetrievedSources);
        LogSearchPage second = await session.NextAsync().ConfigureAwait(true);
        Assert.Equal(["4", "3"], second.Entries.Select(row => row.Entry.Message));
        LogSearchPage third = await session.NextAsync().ConfigureAwait(true);
        Assert.Equal(["2", "1"], third.Entries.Select(row => row.Entry.Message));
        Assert.False(third.HasMore);
        Assert.Empty(third.UnconfirmedSources);
        Assert.Empty((await session.NextAsync().ConfigureAwait(true)).Entries);
    }

    /// <summary>一台の障害・欠測・進まないカーソルで、他クラスタのログを失いません。</summary>
    /// <returns>成功分と未確認先を区別した検索です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PartialFailuresRemainVisibleWithoutBlockingOtherSourcesAsync(int failure)
    {
        LogSearchSource good = new("a", "one"), bad = new("b", "one");
        LogSearchSession session = new(new(), [bad, good], (source, _, _) =>
        {
            if (source == good)
            {
                return Task.FromResult<LogQueryResult?>(new([Entry(1)], null, true));
            }
            return failure switch
            {
                0 => Task.FromException<LogQueryResult?>(new HttpRequestException()),
                1 => Task.FromResult<LogQueryResult?>(null),
                2 => Task.FromResult<LogQueryResult?>(new([], "not-advancing", true)),
                _ => Task.FromResult<LogQueryResult?>(new([], null, false, ["unavailable"]))
            };
        });
        LogSearchPage page = await session.NextAsync().ConfigureAwait(true);
        Assert.Equal(good, Assert.Single(page.Entries).Source);
        Assert.Equal([bad.Label], page.UnconfirmedSources);
        Assert.False(page.Complete);
        Assert.False(page.HasMore);
    }

    /// <summary>空でなくても同じカーソルを再送する取得先を打ち切ります。</summary>
    /// <returns>ループせず未確認へ移す結果です。</returns>
    [Fact]
    public async Task RepeatedCursorDoesNotLoopOrReplayEntriesAsync()
    {
        int reads = 0;
        LogSearchSession session = new(new(Limit: 2), [new("a", "one")], (_, _, _) =>
        {
            reads++;
            return Task.FromResult<LogQueryResult?>(new([Entry(1)], "same", true));
        });
        LogSearchPage page = await session.NextAsync().ConfigureAwait(true);
        Assert.Single(page.Entries);
        Assert.False(page.Complete);
        Assert.False(page.HasMore);
        Assert.Equal(2, reads);
    }

    /// <summary>開始済みの二照会だけを取り消し、別のページを同時に読ませません。</summary>
    /// <returns>取消の伝播と並列数制限です。</returns>
    [Fact]
    public async Task CancellationAndConcurrencyAreBoundedAsync()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        LogSearchSession session = new(new(), Enumerable.Range(0, 8).Select(index => new LogSearchSource("a", index.ToString(System.Globalization.CultureInfo.InvariantCulture))), async (_, _, token) =>
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                started.TrySetResult();
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return null;
        });
        using Task<LogSearchPage> pending = session.NextAsync(cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.NextAsync()).ConfigureAwait(true);
            await cancellation.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending).ConfigureAwait(true);
            Assert.Equal(2, reads);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>クラスタ数に応じてバッファを制限し、一回のページを無制限に保持しません。</summary>
    /// <returns>全取得先の応答上限です。</returns>
    [Fact]
    public async Task LargeFleetSharesABoundedResponseBudgetAsync()
    {
        int bytes = 0;
        LogSearchSession session = new(new(), Enumerable.Range(0, 80).Select(index => new LogSearchSource("a", index.ToString(System.Globalization.CultureInfo.InvariantCulture))), (_, query, _) =>
        {
            Assert.InRange(query.MaximumBytes, 4096, 65536);
            Assert.Equal(100, query.Limit);
            Interlocked.Add(ref bytes, query.MaximumBytes);
            return Task.FromResult<LogQueryResult?>(new([], null, true));
        });
        LogSearchPage page = await session.NextAsync().ConfigureAwait(true);
        Assert.Equal(80, page.RetrievedSources);
        Assert.InRange(bytes, 1, 2097152);
        Assert.True(page.Complete);
    }

    /// <summary>同じPeer IDでもクラスタが異なれば両方の取得先を保持します。</summary>
    [Fact]
    public void DiscoveryIncludesEveryObservablePeer()
    {
        PeerInfo peer = new("same", Guid.NewGuid(), "host", "127.0.0.1", 10000, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow);
        DiscoveredCluster[] clusters = [new("a", [peer, peer with { PeerId = "other" }], [], DateTimeOffset.UtcNow), new("b", [peer], [], DateTimeOffset.UtcNow)];
        Assert.Equal([new("a", "same"), new("a", "other"), new LogSearchSource("b", "same")], LogSearchSession.Discover(clusters.Concat(clusters)));
    }

    /// <summary>同一時刻でもID順が一定になるログを作成します。</summary>
    /// <returns>指定した識別子と内容を持つログです。</returns>
    private static WorkflowLogEntry Entry(int index)
        => new(Guid.Parse($"00000000-0000-0000-0000-{index:D12}"), DateTimeOffset.UnixEpoch, "INFO", "origin", index.ToString(System.Globalization.CultureInfo.InvariantCulture), "workflow", "node");
}
