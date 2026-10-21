using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

/// <summary>保守時刻の差を実行中状態の分岐と混同せず、必要な基準点と未確定予約は照合します。</summary>
internal static class RepositoryStateVersion
{
    /// <summary>ローカル値を除いた現在版を比較します。</summary>
    /// <returns>共有内容識別子です。</returns>
    internal static string HashEntries(IEnumerable<DurableEntry> entries) => ContractJson.Hash(OrderedSharedEntries(entries));

    /// <summary>保存媒体によらず共有候補を同じ順序で識別します。</summary>
    /// <returns>重複を除いた共有現在値と競合候補です。</returns>
    internal static IEnumerable<DurableEntry> OrderedSharedEntries(IEnumerable<DurableEntry> entries)
        => entries.Where(entry => DurableRepository.IsShared(entry.Scope)).Distinct().OrderBy(entry => entry.Scope, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal).ThenBy(entry => entry.Version, StringComparer.Ordinal);

    /// <summary>保持開始前は従来の全共有値、開始後は現在状態と保護された基準点を比較します。</summary>
    /// <returns>保存媒体によらない実行状態の識別子です。</returns>
    internal static string Hash(IEnumerable<DurableEntry> entries, bool retained, IEnumerable<string> protectedHistory, IEnumerable<Guid> pending)
        => WithPending(CurrentHash(entries, retained, protectedHistory), retained, pending);

    /// <summary>予約だけの変更で、検証済みの現在値と保護履歴を再構築しません。</summary>
    /// <returns>予約集合から独立した現在状態のハッシュです。</returns>
    internal static string CurrentHash(IEnumerable<DurableEntry> entries, bool retained, IEnumerable<string> protectedHistory)
        => HashEntries(CurrentEntries(entries, retained, protectedHistory));

    /// <summary>保持開始前後の現在値と復旧基準を、どの計算経路でも同じ規則で選択します。</summary>
    /// <returns>現在状態の照合に必要な共有候補です。</returns>
    internal static IEnumerable<DurableEntry> CurrentEntries(IEnumerable<DurableEntry> entries, bool retained, IEnumerable<string> protectedHistory)
    {
        if (!retained)
        {
            return entries;
        }
        HashSet<string> protectedKeys = protectedHistory.ToHashSet(StringComparer.Ordinal);
        return entries.Where(entry => HistoryRetentionPolicy.Category("entry", entry.Scope) is null
            || protectedKeys.Contains(DurableRepository.GetAddress(entry.Scope, entry.Key) + entry.Version));
    }

    /// <summary>同一取引で読み取った最新の予約を、従来と同じ形式で照合対象へ含めます。</summary>
    /// <returns>既存Peerと同じ状態識別子です。</returns>
    internal static string WithPending(string current, bool retained, IEnumerable<Guid> pending)
        => retained ? ContractJson.Hash(new { Current = current, Pending = pending.Distinct().Order().ToArray() })
                    : current;

    /// <summary>固定した共有書出しと同じ対象を識別します。</summary>
    /// <returns>現状態の検査値です。</returns>
    internal static string Hash(RepositorySnapshot snapshot)
        => Hash(snapshot.Entries, snapshot.HistoryBoundary is not null, snapshot.ProtectedHistory ?? [], snapshot.PendingOperations ?? []);
}
