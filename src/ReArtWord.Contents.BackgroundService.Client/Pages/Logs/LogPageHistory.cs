using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Client.Pages.Logs;

/// <summary>検索条件と取得先を固定し、保持量を制限しながら前後のページを読みます。</summary>
internal sealed class LogPageHistory(LogQuery query, IEnumerable<LogSearchSource> sources,
    Func<LogSearchSource, LogQuery, CancellationToken, Task<LogQueryResult?>> read)
{
    private readonly LogSearchSource[] sources = sources.ToArray();
    private readonly SortedDictionary<int, LogSearchPage> pages = [];
    private LogSearchSession? reader;
    private int nextPage;

    /// <summary>表示中のページ位置。検索前は未確定です。</summary>
    internal int PageIndex
    {
        get; private set;
    } = -1;

    /// <summary>検索時の取得先と時刻の上限を固定し、ページ移動に新着を混ぜません。</summary>
    /// <returns>一回の検索に使う履歴です。</returns>
    internal static async Task<LogPageHistory> CreateAsync(ManagementApi api, LogQuery query, bool allClusters, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LogSearchSource> targets;
        if (allClusters)
        {
            DiscoveredCluster[] clusters = await api.GetForPeerAsync<DiscoveredCluster[]>("/client/clusters", null, null, cancellationToken).ConfigureAwait(false) ?? [];
            targets = LogSearchSession.Discover(clusters);
        }
        else if (api.SelectedClusterId is { Length: > 0 } cluster && api.SelectedPeerId is { Length: > 0 } peer)
        {
            targets = [new(cluster, peer)];
        }
        else
        {
            targets = [];
        }
        DateTimeOffset end = api.UtcNow;
        LogQuery fixedQuery = query;
        if (query.Until is null || end < query.Until)
        {
            fixedQuery = query with { Until = end };
        }
        return new(fixedQuery, targets, (source, filter, token) => api.PostForPeerAsync<LogQueryResult>("/api/logs/query", filter, source.PeerId, source.ClusterId, cancellationToken: token));
    }

    /// <summary>近くのページは再利用し、保持範囲より前へ戻る場合は同じ条件で読み直します。</summary>
    /// <returns>取得に成功したページです。取消時は表示位置を変更しません。</returns>
    internal async Task<LogSearchPage> ReadAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        cancellationToken.ThrowIfCancellationRequested();
        if (pages.TryGetValue(pageIndex, out LogSearchPage? cached))
        {
            PageIndex = pageIndex;
            return cached;
        }
        bool replay = reader is null || pageIndex != nextPage;
        LogSearchSession current = replay ? new(query, sources, read, maximumResultBytes: 524288)
                                          : reader!;
        int index = replay ? 0
                           : nextPage;
        SortedDictionary<int, LogSearchPage> fetched = [];
        LogSearchPage page;

        // 読取り中に取消・失敗したカーソルを再利用しないよう、完了するまで参照を外します。
        reader = null;
        do
        {
            page = await current.NextAsync(cancellationToken).ConfigureAwait(false);
            Remember(fetched, index, page);
            if (index == pageIndex || !page.HasMore)
            {
                break;
            }
            index++;
        }
        while (true);
        cancellationToken.ThrowIfCancellationRequested();
        if (replay)
        {
            pages.Clear();
        }
        foreach ((int position, LogSearchPage item) in fetched)
        {
            Remember(pages, position, item);
        }
        reader = current;
        nextPage = index + 1;
        PageIndex = index;
        return page;
    }

    /// <summary>ログ本文を保持するページ数を制限します。</summary>
    private static void Remember(SortedDictionary<int, LogSearchPage> retained, int index, LogSearchPage page)
    {
        retained[index] = page;
        if (8 < retained.Count)
        {
            retained.Remove(retained.First().Key);
        }
    }
}
