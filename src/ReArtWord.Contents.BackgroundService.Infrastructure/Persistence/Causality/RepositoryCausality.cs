using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;

/// <summary>現在版と競合候補の因果情報を本文と同じ取引へ保存します。</summary>
internal static class RepositoryCausality
{
    /// <summary>旧保存形式でも不明な値を落とさない、Peerローカルの因果索引です。</summary>
    internal static string Scope => "peer:repository-causality";

    /// <summary>一つの共有キーに対応する現在候補の保存キーです。</summary>
    /// <returns>用途とキーの衝突しない組です。</returns>
    internal static string EntryKey(string scope, string key) => "entry:" + DurableRepository.GetAddress(scope, key);

    /// <summary>利用者が指定した変更だけを操作指紋に含めます。</summary>
    /// <returns>旧形式と同じ変更内容の指紋です。</returns>
    internal static string Fingerprint(IReadOnlyList<DurableMutation> mutations)
        => ContractJson.Hash(mutations.Select(mutation => mutation with { CausalVersion = null }).ToArray());

    /// <summary>親版を固定した共有変更へ、複製先でも同一となる因果情報を付けます。</summary>
    /// <returns>呼出元の配列を書き換えない確定候補です。</returns>
    internal static DurableMutation[] Stamp(RepositoryDocument document, IReadOnlyList<DurableMutation> mutations)
    {
        CausalPosition? position = null;
        if (mutations.Any(mutation => DurableRepository.IsShared(mutation.Scope) && mutation.CausalVersion is null))
        {
            position = Next(document);
        }
        return mutations.Select(mutation => StampOne(document, mutation, position)).ToArray();
    }

    /// <summary>一致した現在版と明示解消候補だけを親にします。</summary>
    /// <returns>共有変更の因果情報、または元のローカル変更です。</returns>
    private static DurableMutation StampOne(RepositoryDocument document, DurableMutation mutation, CausalPosition? position)
    {
        if (!DurableRepository.IsShared(mutation.Scope))
        {
            return mutation;
        }
        string address = DurableRepository.GetAddress(mutation.Scope, mutation.Key);
        List<DurableEntry> parents = [];
        if (document.Entries.TryGetValue(address, out DurableEntry? current))
        {
            parents.Add(current);
        }
        if (mutation.ResolveVersions is not null)
        {
            parents.AddRange(document.Conflicts.Where(entry => string.Equals(entry.Scope, mutation.Scope, StringComparison.Ordinal)
                && string.Equals(entry.Key, mutation.Key, StringComparison.Ordinal)));
        }
        string[] clocks = parents.Select(entry => Find(document, entry) ?? CausalClock.Legacy(entry)).ToArray();
        string stamped = mutation.CausalVersion ?? CausalClock.Advance(clocks, position!.Origin.ToString("N"), position.Sequence);
        _ = CausalClock.Read(stamped);
        string[] verifiedParents = parents.Select(entry => Find(document, entry)).OfType<string>().ToArray();
        if (verifiedParents.Any(clock => !CausalClock.Includes(stamped, clock)))
        {
            throw new InvalidDataException("今回の変更が確認済みの親版を包含していません。");
        }
        return mutation with { CausalVersion = stamped };
    }

    /// <summary>一つの保存先の書込み元と、再起動で巻き戻さない順序を進めます。</summary>
    /// <returns>今回の確定変更に使う位置です。</returns>
    private static CausalPosition Next(RepositoryDocument document)
    {
        string positionKey = "position";
        DurableEntry? entry = document.Entries.GetValueOrDefault(DurableRepository.GetAddress(Scope, positionKey));
        CausalPosition previous = entry?.Value?.Json.Deserialize<CausalPosition>() ?? new(Guid.NewGuid(), 0);
        CausalPosition next = previous with { Sequence = checked(previous.Sequence + 1) };
        Save(document, positionKey, NodeValue.FromObject(next));
        return next;
    }

    /// <summary>同じ本文版を複数経路から確認した場合は両方の因果情報を保持します。</summary>
    internal static void Remember(RepositoryDocument document, RepositoryCausalEntry evidence)
    {
        _ = CausalClock.Read(evidence.Clock);
        List<RepositoryCausalEntry> known = Read(document, evidence.Scope, evidence.Key).ToList();
        RepositoryCausalEntry? previous = known.FirstOrDefault(item => string.Equals(item.Version, evidence.Version, StringComparison.Ordinal));
        if (previous is not null)
        {
            known.Remove(previous);
            evidence = evidence with { Clock = JsonSerializer.Serialize(CausalClock.Merge([previous.Clock, evidence.Clock])) };
        }
        known.Add(evidence);
        Save(document, EntryKey(evidence.Scope, evidence.Key), NodeValue.FromObject(known.OrderBy(item => item.Version, StringComparer.Ordinal).ToArray()));
    }

    /// <summary>取込み本文と対応しない因果情報を受け入れません。</summary>
    internal static void Import(RepositoryDocument document, RepositorySnapshot snapshot)
    {
        HashSet<(string Scope, string Key, string Version)> present = snapshot.Entries.Select(entry => (entry.Scope, entry.Key, entry.Version)).ToHashSet();
        foreach (RepositoryCausalEntry evidence in snapshot.CausalEntries ?? [])
        {
            if (!present.Contains((evidence.Scope, evidence.Key, evidence.Version)) || !DurableRepository.IsShared(evidence.Scope))
            {
                throw new InvalidDataException("因果情報に対応する共有本文がありません。");
            }
            Remember(document, evidence);
        }
    }

    /// <summary>不要になった過去版の因果情報を現在候補の索引へ残しません。</summary>
    internal static void Trim(RepositoryDocument document, IEnumerable<DurableEntry> changed)
    {
        foreach (IGrouping<(string Scope, string Key), DurableEntry> group in changed.GroupBy(entry => (entry.Scope, entry.Key)))
        {
            if (!DurableRepository.IsShared(group.Key.Scope))
            {
                continue;
            }
            HashSet<string> versions = document.Conflicts.Where(entry => string.Equals(entry.Scope, group.Key.Scope, StringComparison.Ordinal)
                && string.Equals(entry.Key, group.Key.Key, StringComparison.Ordinal)).Select(entry => entry.Version).ToHashSet(StringComparer.Ordinal);
            if (document.Entries.TryGetValue(DurableRepository.GetAddress(group.Key.Scope, group.Key.Key), out DurableEntry? current))
            {
                versions.Add(current.Version);
            }
            RepositoryCausalEntry[] retained = Read(document, group.Key.Scope, group.Key.Key).Where(entry => versions.Contains(entry.Version)).ToArray();
            if (retained.Length != 0 || document.Entries.ContainsKey(DurableRepository.GetAddress(Scope, EntryKey(group.Key.Scope, group.Key.Key))))
            {
                Save(document, EntryKey(group.Key.Scope, group.Key.Key), NodeValue.FromObject(retained));
            }
        }
    }

    /// <summary>両側に因果情報があるときだけ、全本文履歴を走査せず判定します。</summary>
    /// <returns>確認結果、または旧形式の祖先検証が必要なnullです。</returns>
    internal static bool? IsAncestor(RepositoryDocument document, DurableEntry ancestor, DurableEntry descendant)
    {
        string? before = Find(document, ancestor);
        string? after = Find(document, descendant);
        if (before is not null && after is not null)
        {
            return CausalClock.Includes(after, before) && (!CausalClock.Includes(before, after) || RepositoryEquivalentValues.Includes(descendant, ancestor));
        }
        if (after is not null && CausalClock.Includes(after, CausalClock.Legacy(ancestor)))
        {
            return true;
        }
        return null;
    }

    /// <summary>現在候補に対応する情報だけを同期へ渡します。</summary>
    /// <returns>保存されている因果情報です。</returns>
    internal static RepositoryCausalEntry[] Export(RepositoryDocument document, IEnumerable<DurableEntry> entries)
        => entries.Where(entry => DurableRepository.IsShared(entry.Scope)).Select(entry => new { Entry = entry, Clock = Find(document, entry) })
            .Where(item => item.Clock is not null).Select(item => new RepositoryCausalEntry(item.Entry.Scope, item.Entry.Key, item.Entry.Version, item.Clock!)).Distinct().ToArray();

    /// <summary>一項目の候補集合から、指定版の位置を読みます。</summary>
    /// <returns>対応する因果情報、旧形式ならnullです。</returns>
    internal static string? Find(RepositoryDocument document, DurableEntry entry)
        => Read(document, entry.Scope, entry.Key).FirstOrDefault(item => string.Equals(item.Version, entry.Version, StringComparison.Ordinal))?.Clock;

    /// <summary>一つのキーに属する候補だけを検証して読みます。</summary>
    /// <returns>現在値と競合候補の因果情報です。</returns>
    private static RepositoryCausalEntry[] Read(RepositoryDocument document, string scope, string key)
    {
        DurableEntry? entry = document.Entries.GetValueOrDefault(DurableRepository.GetAddress(Scope, EntryKey(scope, key)));
        RepositoryCausalEntry[] values = entry?.Value?.Json.Deserialize<RepositoryCausalEntry[]>() ?? [];
        if (values.Any(value => !string.Equals(value.Scope, scope, StringComparison.Ordinal) || !string.Equals(value.Key, key, StringComparison.Ordinal))
            || values.Select(value => value.Version).Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new InvalidDataException("現在候補の因果索引が不正です。");
        }
        return values;
    }

    /// <summary>本文の保存と同じ取引に、同じ内容で安定したメタデータを加えます。</summary>
    private static void Save(RepositoryDocument document, string key, NodeValue value)
    {
        document.Entries[DurableRepository.GetAddress(Scope, key)] = new(Scope, key, ContractJson.Hash(value), value);
    }

    /// <summary>同じ保存先の書込み元と単調な確定位置です。</summary>
    private sealed record CausalPosition(Guid Origin, long Sequence);
}
