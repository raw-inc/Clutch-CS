namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

/// <summary>一つの保存先の購読対象だけを索引化します。</summary>
internal sealed class DurableStateHub(string path)
{
    private readonly Dictionary<string, HashSet<DurableStateSubscription>> subscriptions = new(StringComparer.Ordinal);

    /// <summary>所有中の保存先です。</summary>
    internal string Path => path;

    /// <summary>初期値の確定とコミット順を揃える排他です。</summary>
    internal Lock Gate
    {
        get;
    } = new();

    /// <summary>購読・コミットによる保持数です。登録元の排他内だけで変更します。</summary>
    internal int References
    {
        get; set;
    }

    /// <summary>JSON文書の更新前に、購読中のキーの版だけを比較元として取得します。</summary>
    /// <returns>全履歴を列挙しない比較元です。</returns>
    internal Dictionary<string, string?> Capture(Documents.RepositoryDocument document)
    {
        using (Gate.EnterScope())
        {
            return subscriptions.Keys.ToDictionary(address => address, address => document.Entries.GetValueOrDefault(address)?.Version, StringComparer.Ordinal);
        }
    }

    /// <summary>指定キー以外の変更で待ち受けを起こしません。</summary>
    internal void Add(DurableStateSubscription subscription)
    {
        using (Gate.EnterScope())
        {
            if (!subscriptions.TryGetValue(subscription.Address, out HashSet<DurableStateSubscription>? group))
            {
                group = [];
                subscriptions.Add(subscription.Address, group);
            }
            group.Add(subscription);
        }
    }

    /// <summary>利用終了したキーも保持しません。</summary>
    internal void Remove(DurableStateSubscription subscription)
    {
        using (Gate.EnterScope())
        {
            if (subscriptions.TryGetValue(subscription.Address, out HashSet<DurableStateSubscription>? group))
            {
                group.Remove(subscription);
                if (group.Count == 0)
                {
                    subscriptions.Remove(subscription.Address);
                }
            }
        }
        DurableStateNotifications.Release(this);
    }

    /// <summary>所有コピーを一つだけ作り、コミット排他内で有限キューへ渡します。</summary>
    internal void Publish(DurableEntry entry)
    {
        if (!subscriptions.TryGetValue(DurableRepository.GetAddress(entry.Scope, entry.Key), out HashSet<DurableStateSubscription>? group))
        {
            return;
        }
        long bytes = entry.Value?.PayloadSize() ?? 0;
        DurableEntry owned = entry;
        if (bytes <= DurableStateSubscription.MaximumBytes)
        {
            owned = entry with { Value = entry.Value?.Copy() };
        }
        foreach (DurableStateSubscription subscription in group)
        {
            subscription.Publish(owned, bytes);
        }
    }
}
