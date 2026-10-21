using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>各取得先のカーソルを固定し、転送済みの重複を除いた日時順のページを返します。</summary>
public sealed class LogSearchSession
{
    private readonly LogQuery query;
    private readonly Source[] sources;
    private readonly int pageBytes;
    private readonly int resultBytes;
    private readonly Func<LogSearchSource, LogQuery, CancellationToken, Task<LogQueryResult?>> read;
    private ScopedLogEntry? last;
    private int busy;

    /// <summary>検索条件と取得先を一回の検索へ固定します。</summary>
    public LogSearchSession(LogQuery query, IEnumerable<LogSearchSource> sources, Func<LogSearchSource, LogQuery, CancellationToken, Task<LogQueryResult?>> read,
        int bufferBytes = 2097152, int maximumPageBytes = 65536, int maximumResultBytes = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferBytes, 16384);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPageBytes, 16384);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPageBytes, 1048576);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResultBytes, 16384);
        resultBytes = maximumResultBytes;
        this.query = query;
        this.sources = sources.Distinct().OrderBy(source => source.ClusterId, StringComparer.Ordinal).ThenBy(source => source.PeerId, StringComparer.Ordinal)
            .Select(source => new Source(source)).ToArray();
        this.read = read;
        pageBytes = Math.Min(query.MaximumBytes, Math.Clamp(bufferBytes / Math.Max(1, this.sources.Length), 16384, maximumPageBytes));
    }

    /// <summary>観測中の各Peerを取得先とし、同じPeer IDでも別クラスタなら区別します。</summary>
    /// <returns>現在の観測から得られた固定取得先です。</returns>
    public static IReadOnlyList<LogSearchSource> Discover(IEnumerable<DiscoveredCluster> clusters)
        => clusters.SelectMany(cluster => cluster.Peers.Select(peer => new LogSearchSource(cluster.ClusterId, peer.PeerId))).Distinct().ToArray();

    /// <summary>必要な取得先だけを補充し、古いページへ新着を混入させません。</summary>
    /// <returns>元の取得先情報を持ち、要求件数と任意の本文上限内に収めた次ページです。</returns>
    public async Task<LogSearchPage> NextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref busy, 1) != 0)
        {
            throw new InvalidOperationException("A log search page is already being read.");
        }
        try
        {
            List<ScopedLogEntry> result = [];
            long bytes = 0;
            int limit = Math.Clamp(query.Limit, 1, 1000);
            while (result.Count < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Source[] pending = sources.Where(source => source.Entries.Count == 0 && !source.Exhausted).ToArray();
                if (pending.Length != 0)
                {
                    await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
                        async (source, token) => await ReadAsync(source, token).ConfigureAwait(false)).ConfigureAwait(false);
                }
                Source? next = null;
                foreach (Source source in sources)
                {
                    if (source.Entries.Count != 0 && (next is null || Compare(source.Entries.Peek(), next.Entries.Peek()) < 0))
                    {
                        next = source;
                    }
                }
                if (next is null)
                {
                    break;
                }
                ScopedLogEntry entry = next.Entries.Peek();
                if (last is not null && Compare(entry, last) == 0)
                {
                    next.Entries.Dequeue();
                    continue;
                }
                if (resultBytes != int.MaxValue)
                {
                    long size = Serialization.JsonSize.Measure(entry.Entry);
                    if (result.Count != 0 && resultBytes < bytes + size)
                    {
                        break;
                    }
                    bytes += size;
                }
                next.Entries.Dequeue();
                last = entry;
                result.Add(entry);
            }
            string[] unconfirmed = sources.Where(source => source.Unconfirmed).Select(source => source.Target.Label).ToArray();
            return new(result, sources.Any(source => source.Entries.Count != 0 || !source.Exhausted), sources.Length != 0 && unconfirmed.Length == 0,
                unconfirmed, sources.Length, sources.Count(source => source.Retrieved));
        }
        finally
        {
            Volatile.Write(ref busy, 0);
        }
    }

    /// <summary>一取得先の読取り障害を他クラスタへ伝播させず、固定カーソルを進めます。</summary>
    /// <returns>有限バッファの補充、または未確認状態の記録です。</returns>
    private async Task ReadAsync(Source source, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            LogQueryResult? page = await read(source.Target, query with { Cursor = source.Cursor, Limit = Math.Clamp(query.Limit, 1, 1000), MaximumBytes = pageBytes }, timeout.Token).ConfigureAwait(false);
            if (page is null)
            {
                source.Unconfirmed = source.Exhausted = true;
                return;
            }
            source.Retrieved = true;
            source.Unconfirmed |= !page.Complete || page.UnavailablePeers is { Count: > 0 };
            if (page.Cursor is not null && (page.Entries.Count == 0 || string.Equals(page.Cursor, source.Cursor, StringComparison.Ordinal)))
            {
                source.Unconfirmed = source.Exhausted = true;
                return;
            }
            Dictionary<Guid, LogEntryOmission> omissions = (page.Omissions ?? []).ToDictionary(item => item.EntryId);
            foreach (WorkflowLogEntry entry in page.Entries)
            {
                source.Entries.Enqueue(new(source.Target, entry, omissions.GetValueOrDefault(entry.Id)));
            }
            source.Cursor = page.Cursor;
            source.Exhausted = page.Cursor is null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            source.Unconfirmed = source.Exhausted = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            source.Unconfirmed = source.Exhausted = true;
        }
    }

    /// <summary>取得元Peerが異なる同一ログも隣接するよう、時刻・ID・クラスタで比較します。</summary>
    /// <returns>新しいログを先にする順序です。</returns>
    private static int Compare(ScopedLogEntry left, ScopedLogEntry right)
    {
        int result = right.Entry.Time.CompareTo(left.Entry.Time);
        if (result == 0)
        {
            result = right.Entry.Id.CompareTo(left.Entry.Id);
        }
        if (result == 0)
        {
            result = string.Compare(left.Source.ClusterId, right.Source.ClusterId, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>一つのPeerが所有する継続位置と、指定された取得予算内の応答バッファです。</summary>
    private sealed class Source(LogSearchSource target)
    {
        internal LogSearchSource Target
        {
            get;
        } = target;

        internal Queue<ScopedLogEntry> Entries
        {
            get;
        } = new();

        internal string? Cursor
        {
            get;
            set;
        }

        internal bool Exhausted
        {
            get;
            set;
        }

        internal bool Unconfirmed
        {
            get;
            set;
        }

        internal bool Retrieved
        {
            get;
            set;
        }

        /// <summary>バッファ本文を含めず、取得先を表示します。</summary>
        /// <returns>クラスタとPeerの識別子です。</returns>
        public override string ToString() => Target.Label;
    }
}
