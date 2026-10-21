namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>要求IDを有限の分単位で保持し、無通信後も全IDの期限走査を実行経路へ載せません。</summary>
internal sealed class RuntimeRequestWindow
{
    private readonly Dictionary<long, HashSet<Guid>> buckets = [];

    /// <summary>まだ受付可能な期限に属するID数です。</summary>
    internal int Count
    {
        get; private set;
    }

    /// <summary>最大十数個の期限集合だけを調べます。呼出元の排他で使用します。</summary>
    /// <returns>現在実体で既に受け付けたIDかです。</returns>
    internal bool Contains(Guid id) => buckets.Values.Any(values => values.Contains(id));

    /// <summary>本文から延長できないID由来の期限へ一度だけ登録します。</summary>
    internal void Add(Guid id, DateTimeOffset deadline)
    {
        long bucket = deadline.UtcTicks / TimeSpan.TicksPerMinute + 1;
        if (!buckets.TryGetValue(bucket, out HashSet<Guid>? values))
        {
            values = [];
            buckets.Add(bucket, values);
        }
        if (values.Add(id))
        {
            Count++;
        }
    }

    /// <summary>期限切れ集合の参照だけを解放し、一件ずつヒープから取り出しません。</summary>
    internal void Expire(DateTimeOffset now)
    {
        long minute = now.UtcTicks / TimeSpan.TicksPerMinute;
        foreach (long bucket in buckets.Keys.Where(key => key <= minute).ToArray())
        {
            Count -= buckets[bucket].Count;
            buckets.Remove(bucket);
        }
    }
}
