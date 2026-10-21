using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;

/// <summary>保存先ごとのコミット順を保ち、表示や後続処理を保存の排他区間で実行しません。</summary>
internal static class DurableStateNotifications
{
    private static readonly Lock access = new();
    private static readonly Dictionary<string, DurableStateHub> hubs = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase
                                                                                                       : StringComparer.Ordinal);

    /// <summary>未購読の履歴や診断値を変更キューへ保持しません。</summary>
    /// <returns>ユーザー状態として通知する保存範囲かです。</returns>
    internal static bool IsState(string? scope) => scope is not null && (NodeStateScope.IsShared(scope)
        || scope.StartsWith("peer:", StringComparison.Ordinal) && scope.Contains(":workflow:", StringComparison.Ordinal));

    /// <summary>購読とコミットの間だけ保存先の通知口を保持します。</summary>
    /// <returns>参照中の通知口です。</returns>
    internal static DurableStateHub Acquire(string path)
    {
        using (access.EnterScope())
        {
            if (!hubs.TryGetValue(path, out DurableStateHub? hub))
            {
                hub = new(path);
                hubs.Add(path, hub);
            }
            hub.References++;
            return hub;
        }
    }

    /// <summary>最後の購読またはコミットが終了した保存先への参照を除去します。</summary>
    internal static void Release(DurableStateHub hub)
    {
        using (access.EnterScope())
        {
            hub.References--;
            if (hub.References == 0)
            {
                hubs.Remove(hub.Path);
            }
        }
    }

    /// <summary>JSON取引の購読対象だけを比較し、未購読時は文書を走査しません。</summary>
    /// <returns>対象ごとの取引前の版です。</returns>
    internal static Dictionary<string, string?>? Capture(string path, Documents.RepositoryDocument document)
    {
        using (access.EnterScope())
        {
            return hubs.GetValueOrDefault(path)?.Capture(document);
        }
    }

    /// <summary>JSON置換後の変更キーだけを、同じファイル排他内で通知します。</summary>
    internal static void Publish(string path, Documents.RepositoryDocument document, Dictionary<string, string?>? before)
    {
        if (before is null)
        {
            return;
        }
        DurableEntry[] changed = before.Where(pair => document.Entries.TryGetValue(pair.Key, out DurableEntry? value)
            && !string.Equals(pair.Value, value.Version, StringComparison.Ordinal)).Select(pair => document.Entries[pair.Key]).ToArray();
        Commit(path, changed, static () =>
        {
        });
    }

    /// <summary>DB読取りをロック内で行わず、購読だけを先に登録します。</summary>
    /// <returns>初期値との照合を待つ購読です。</returns>
    internal static DurableStateSubscription Subscribe(string path, string scope, string key)
    {
        DurableStateHub hub = Acquire(path);
        DurableStateSubscription subscription = new(hub, DurableRepository.GetAddress(scope, key));
        hub.Add(subscription);
        return subscription;
    }

    /// <summary>コミットと配信を同じ通知排他内で順序づけます。配信は有限キューへの追加だけです。</summary>
    internal static void Commit(string path, IReadOnlyCollection<DurableEntry>? changes, Action commit)
    {
        if (changes is null || changes.Count == 0)
        {
            commit();
            return;
        }
        DurableStateHub hub = Acquire(path);
        try
        {
            using (hub.Gate.EnterScope())
            {
                commit();
                foreach (DurableEntry entry in changes)
                {
                    hub.Publish(entry);
                }
            }
        }
        finally
        {
            Release(hub);
        }
    }
}
