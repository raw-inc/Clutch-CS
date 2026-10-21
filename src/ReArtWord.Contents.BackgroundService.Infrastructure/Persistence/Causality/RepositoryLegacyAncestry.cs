using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;

// LEGACY-FORMAT: repository-ancestry-v1 — 旧祖先索引は移行前の全分岐が保持対象外となったことを確認してから撤去します。
/// <summary>因果位置を持たなかった旧版だけを、本文履歴から小さい索引へ移します。</summary>
internal static class RepositoryLegacyAncestry
{
    /// <summary>旧版の本文を破棄する前に、同じ項目の親と検査値だけを保存します。</summary>
    internal static void Remember(RepositoryDocument document, IEnumerable<RepositoryLegacyEntry> incoming)
    {
        foreach (IGrouping<(string Scope, string Key), RepositoryLegacyEntry> group in incoming.GroupBy(entry => (entry.Scope, entry.Key)))
        {
            string key = "legacy:" + DurableRepository.GetAddress(group.Key.Scope, group.Key.Key);
            string address = DurableRepository.GetAddress(RepositoryCausality.Scope, key);
            RepositoryLegacyEntry[] values = Read(document, group.Key.Scope, group.Key.Key).Concat(group).DistinctBy(entry => (entry.Version, entry.ValueHash))
                .OrderBy(entry => entry.Version, StringComparer.Ordinal).ThenBy(entry => entry.ValueHash, StringComparer.Ordinal).ToArray();
            NodeValue value = NodeValue.FromObject(values);
            document.Entries[address] = new(RepositoryCausality.Scope, key, ContractJson.Hash(value), value);
        }
    }

    /// <summary>現在状態の因果位置に含まれる移行基準から旧版を遡ります。</summary>
    /// <returns>本文と旧祖先経路を確認できたかです。</returns>
    internal static bool IsAncestor(RepositoryDocument document, DurableEntry ancestor, DurableEntry descendant)
    {
        RepositoryLegacyEntry[] entries = Read(document, ancestor.Scope, ancestor.Key);
        Dictionary<string, RepositoryLegacyEntry[]> byVersion = entries.GroupBy(entry => entry.Version).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        string? clock = RepositoryCausality.Find(document, descendant);
        Queue<string> pending = new((descendant.MergedVersions ?? []).Append(descendant.ParentVersion).OfType<string>());
        if (clock is not null)
        {
            foreach (RepositoryLegacyEntry entry in entries)
            {
                if (CausalClock.Includes(clock, CausalClock.Legacy(new(entry.Scope, entry.Key, entry.Version, null))))
                {
                    pending.Enqueue(entry.Version);
                }
            }
        }
        HashSet<string> visited = new(StringComparer.Ordinal);
        string hash = ContractJson.Hash(ancestor.Value);
        while (pending.TryDequeue(out string? version))
        {
            if (!visited.Add(version) || !byVersion.TryGetValue(version, out RepositoryLegacyEntry[]? candidates))
            {
                continue;
            }
            foreach (RepositoryLegacyEntry entry in candidates)
            {
                if (string.Equals(entry.Version, ancestor.Version, StringComparison.Ordinal) && string.Equals(entry.ValueHash, hash, StringComparison.Ordinal))
                {
                    return true;
                }
                foreach (string parent in entry.Parents)
                {
                    pending.Enqueue(parent);
                }
            }
        }
        return false;
    }

    /// <summary>索引化済みの旧版だけを同期します。</summary>
    /// <returns>現在キーに対応する旧祖先です。</returns>
    internal static RepositoryLegacyEntry[] Export(RepositoryDocument document, IEnumerable<DurableEntry> entries)
        => entries.Where(entry => DurableRepository.IsShared(entry.Scope)).DistinctBy(entry => (entry.Scope, entry.Key))
            .SelectMany(entry => Read(document, entry.Scope, entry.Key)).ToArray();

    /// <summary>本文を保存し直さず、移行前の一版を表現します。</summary>
    /// <returns>元の版と親の索引です。</returns>
    internal static RepositoryLegacyEntry Describe(DurableEntry entry)
        => new(entry.Scope, entry.Key, entry.Version, ContractJson.Hash(entry.Value), (entry.MergedVersions ?? []).Append(entry.ParentVersion).OfType<string>().Distinct(StringComparer.Ordinal).ToArray());

    /// <summary>一キーの移行前履歴を読みます。</summary>
    /// <returns>未移行なら空の索引です。</returns>
    private static RepositoryLegacyEntry[] Read(RepositoryDocument document, string scope, string key)
        => document.Entries.GetValueOrDefault(DurableRepository.GetAddress(RepositoryCausality.Scope, "legacy:" + DurableRepository.GetAddress(scope, key)))
            ?.Value?.Json.Deserialize<RepositoryLegacyEntry[]>() ?? [];
}
