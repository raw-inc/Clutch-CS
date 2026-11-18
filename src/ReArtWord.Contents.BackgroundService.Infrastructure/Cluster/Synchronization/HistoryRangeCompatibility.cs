using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>区間を読めない相手には元のIDを個別に渡し、退役の証拠を省略しません。</summary>
internal static class HistoryRangeCompatibility
{
    /// <summary>旧形式への展開だけで無制限にメモリを使わないための、一転送の上限です。</summary>
    internal static int MaximumExpandedIds => 65536;

    /// <summary>元IDとキー表記を保って旧形式に展開します。大きすぎる場合は相手の更新を要求します。</summary>
    /// <returns>通常の本文と指紋を変更せず、個別の再実行防止記録を追加した固定版です。</returns>
    internal static RepositorySnapshot Expand(RepositorySnapshot snapshot)
    {
        IReadOnlyList<RetiredIdRange> ranges = snapshot.RetiredRanges ?? [];
        if (ranges.Count == 0)
        {
            return snapshot;
        }
        ulong count = 0;
        foreach (RetiredIdRange range in ranges)
        {
            range.Validate();
            count += (ulong)range.Last - range.First + 1;
            if ((ulong)MaximumExpandedIds < count)
            {
                throw new SyncFailureException("Sync.HistoryUpgradeRequired");
            }
        }
        Dictionary<Guid, RetiredOperation> operations = (snapshot.RetiredOperations ?? []).ToDictionary(item => item.Id);
        HashSet<RetiredHistoryKey> keys = new(snapshot.RetiredKeys ?? []);
        foreach (RetiredIdRange range in ranges)
        {
            for (ulong value = range.First; value <= range.Last; value++)
            {
                if (range.Scope is null)
                {
                    Guid id = range.Identity((uint)value);

                    // 本文の指紋や成功結果は復元できません。既存の詳細記録を優先し、
                    // 区間しかない操作は「削除済みで結果不明」として旧版にも再実行を拒否させます。
                    operations.TryAdd(id, new(id, string.Empty, range.Shared, Reason: RepositoryHistoryGuard.ExpiredReason));
                }
                else
                {
                    keys.Add(new(range.Scope, range.KeyAt((uint)value)));
                }
            }
        }
        return snapshot with { RetiredRanges = null, RetiredOperations = operations.Values.ToArray(), RetiredKeys = keys.ToArray() };
    }
}
