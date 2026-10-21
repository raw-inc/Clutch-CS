using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

/// <summary>値が完全に同じ分岐だけを、受信順やPeerに依存しない同じ現在版へ統合します。</summary>
internal static class RepositoryEquivalentValues
{
    /// <summary>取込みで触れたキーだけを調べ、異なる値が一つでもあれば人による解消へ残します。</summary>
    internal static void Merge(RepositoryDocument document, IEnumerable<DurableEntry> incoming)
    {
        HashSet<(string Scope, string Key)> keys = incoming.Select(entry => (entry.Scope, entry.Key)).ToHashSet();
        foreach (IGrouping<(string Scope, string Key), DurableEntry> group in document.Conflicts.GroupBy(entry => (entry.Scope, entry.Key)).Where(group => keys.Contains(group.Key)).ToArray())
        {
            (string scope, string key) = group.Key;
            string address = DurableRepository.GetAddress(scope, key);
            if (!document.Entries.TryGetValue(address, out DurableEntry? current))
            {
                continue;
            }
            DurableEntry[] conflicts = group.ToArray();
            string hash = ContractJson.Hash(current.Value);
            if (conflicts.Length == 0 || conflicts.Any(entry => !string.Equals(hash, ContractJson.Hash(entry.Value), StringComparison.Ordinal)))
            {
                continue;
            }
            DurableEntry[] candidates = [current, .. conflicts];
            string[] parents = candidates.SelectMany(Parents).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            string version = Version(current, parents);
            string clock = JsonSerializer.Serialize(CausalClock.Merge(candidates.Select(entry => RepositoryCausality.Find(document, entry) ?? CausalClock.Legacy(entry))));
            document.Entries[address] = new(scope, key, version, current.Value, MergedVersions: parents);
            document.Conflicts.RemoveAll(entry => string.Equals(entry.Scope, scope, StringComparison.Ordinal) && string.Equals(entry.Key, key, StringComparison.Ordinal));
            RepositoryCausality.Remember(document, new(scope, key, version, clock));
        }
    }

    /// <summary>同じ因果位置の同値分岐も、明示して統合した元版だけを祖先として扱います。</summary>
    /// <returns>検証できる同値統合に元版が含まれるかです。</returns>
    internal static bool Includes(DurableEntry descendant, DurableEntry ancestor)
        => IsDerived(descendant) && descendant.MergedVersions!.Contains(ancestor.Version, StringComparer.Ordinal)
            && string.Equals(ContractJson.Hash(descendant.Value), ContractJson.Hash(ancestor.Value), StringComparison.Ordinal);

    /// <summary>通常の操作版を展開せず、この規則で生成した統合版だけを平坦化します。</summary>
    /// <returns>元版集合と値から同じ識別子を再計算できるかです。</returns>
    private static bool IsDerived(DurableEntry entry)
        => entry.MergedVersions is { Count: > 0 } && string.Equals(entry.Version, Version(entry, entry.MergedVersions), StringComparison.Ordinal);

    /// <summary>同じ値の中間統合だけを元候補へ戻します。</summary>
    /// <returns>統合対象となる元版です。</returns>
    private static IEnumerable<string> Parents(DurableEntry entry)
    {
        if (IsDerived(entry))
        {
            return entry.MergedVersions!;
        }
        return [entry.Version];
    }

    /// <summary>統合の中間版を増やさず、同じ元候補集合には同じ版を付けます。</summary>
    /// <returns>壁時計や書込み元に依存しない識別子です。</returns>
    private static string Version(DurableEntry entry, IReadOnlyList<string> parents)
        => "equal:" + ContractJson.Hash(new { entry.Scope, entry.Key, Value = entry.Value, Parents = parents });
}
