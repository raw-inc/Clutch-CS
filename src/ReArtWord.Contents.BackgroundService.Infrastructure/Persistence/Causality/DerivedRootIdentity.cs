using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;

/// <summary>自動操作の原因となった保存版から生成方式を選び、既存の候補へ新方式を遡及適用しません。</summary>
internal static class DerivedRootIdentity
{
    private static string Prefix => "derived-id-v8:";

    /// <summary>新たな書込みの世代を、既存の因果情報と同じ取引・複製で保持します。</summary>
    /// <returns>旧世代の位置を親に残した、新世代の書込み元です。</returns>
    internal static string Origin(Guid origin) => Prefix + origin.ToString("N");

    /// <summary>更新後に作られた原因版だけを新方式へ進めます。結果記録の有無からは方式を選びません。</summary>
    /// <returns>元候補に固定した自動操作IDです。</returns>
    internal static async Task<Guid> CreateAsync(IDurableRepository repository, IReadOnlyList<DurableEntry> candidates,
        string hash, CancellationToken cancellationToken = default)
    {
        EDerivedIdFormat format = EDerivedIdFormat.Legacy;
        foreach (IGrouping<(string Scope, string Key), DurableEntry> group in candidates.GroupBy(entry => (entry.Scope, entry.Key)))
        {
            DurableEntry? saved = await repository.ReadAsync(RepositoryCausality.Scope,
                RepositoryCausality.EntryKey(group.Key.Scope, group.Key.Key), cancellationToken).ConfigureAwait(false);
            RepositoryCausalEntry[] evidence = RepositoryCausality.ReadValue(saved?.Value?.Json, group.Key.Scope, group.Key.Key);
            HashSet<string> versions = group.Select(entry => entry.Version).ToHashSet(StringComparer.Ordinal);
            if (evidence.Where(entry => versions.Contains(entry.Version)).Any(entry => CausalClock.Read(entry.Clock).Keys.Any(origin => origin.StartsWith(Prefix, StringComparison.Ordinal))))
            {
                format = EDerivedIdFormat.Uuid8;
            }
        }
        return DerivedOperationIdentity.FromHash(Convert.FromHexString(hash), format);
    }
}
