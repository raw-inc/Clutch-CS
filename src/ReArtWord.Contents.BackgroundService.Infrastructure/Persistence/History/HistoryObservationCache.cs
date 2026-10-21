namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>検証済みの圧縮内容に対応する索引だけを保持し、大きな展開本文を保持しません。</summary>
internal sealed class HistoryObservationCache
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Block> blocks = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private long bytes;

    /// <summary>現在の圧縮バイト列を検証したハッシュと展開サイズが一致する索引を返します。</summary>
    /// <returns>未検証または保持対象外ならnullです。</returns>
    internal Block? Find(string hash, int length)
    {
        using (gate.EnterScope())
        {
            Block? block = blocks.GetValueOrDefault(hash);
            return block?.Length == length ? block
                                           : null;
        }
    }

    /// <summary>展開と全行検証の完了後だけ、最大8MiB・128ブロックの索引を保持します。</summary>
    /// <returns>この観測で検証済みの索引です。</returns>
    internal Block Remember(string hash, int length, IReadOnlyList<HistoryRow> rows)
    {
        Header[] headers = rows.Select(row => new Header(row.Kind, row.Key, row.Scope, row.Hash)).ToArray();
        long cost = 256 + headers.Sum(row => 96L + 2L * (row.Kind.Length + row.Key.Length + (row.Scope?.Length ?? 0) + row.Hash.Length));
        Block block = new(length, headers, cost);
        long maximumBytes = 8388608;
        if (maximumBytes < cost)
        {
            return block;
        }
        using (gate.EnterScope())
        {
            if (blocks.ContainsKey(hash))
            {
                return block;
            }
            while (order.Count != 0 && (128 <= blocks.Count || maximumBytes - bytes < cost))
            {
                string oldest = order.Dequeue();
                bytes -= blocks[oldest].Cost;
                blocks.Remove(oldest);
            }
            blocks.Add(hash, block);
            order.Enqueue(hash);
            bytes += cost;
        }
        return block;
    }

    /// <summary>本文を参照しない、元の索引照合情報です。</summary>
    internal sealed record Header(string Kind, string Key, string? Scope, string Hash);

    /// <summary>検証した展開サイズと、全行の小さな照合情報です。</summary>
    internal sealed record Block(int Length, IReadOnlyList<Header> Rows, long Cost);
}
