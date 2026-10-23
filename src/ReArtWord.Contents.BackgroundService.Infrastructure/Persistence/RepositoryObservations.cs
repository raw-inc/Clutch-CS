using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>専用読取りを持たない媒体でも、通常の検証済み列挙から同じ観測を作ります。</summary>
internal static class RepositoryObservations
{
    /// <summary>専用読取りのない媒体では、既存の読取りと競合検査を維持します。</summary>
    /// <returns>指定項目と同じ用途の競合候補です。</returns>
    internal static async ValueTask<(DurableEntry? Entry, IReadOnlyList<DurableEntry> Conflicts)> ReadWithConflictsAsync(IDurableRepository repository, string scope, string key, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> conflicts = await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false);
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        return (entry, conflicts);
    }

    /// <summary>保存文書の個別照会と一括照会で同じ競合候補を返します。</summary>
    /// <returns>対象用途の競合と現在値です。</returns>
    internal static DurableEntry[] ReadConflicts(Documents.RepositoryDocument document, string scope)
        => document.Conflicts.Where(entry => scope.Length == 0 || string.Equals(entry.Scope, scope, StringComparison.Ordinal))
            .SelectMany(entry => new[] { entry, document.Entries[DurableRepository.GetAddress(entry.Scope, entry.Key)] }).Distinct().ToArray();

    /// <summary>媒体の読取り規則を維持し、本文に変化がある項目だけを返します。</summary>
    /// <returns>削除マーカーを含む現在の対象集合です。物理削除した項目は含めません。</returns>
    internal static async ValueTask<IReadOnlyList<DurableObservation>> ReadAsync(IDurableRepository repository, string scope,
        IReadOnlyDictionary<string, string> known, IReadOnlySet<string>? keys, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(known);
        cancellationToken.ThrowIfCancellationRequested();
        if (keys is { Count: 0 })
        {
            return [];
        }
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        List<DurableObservation> result = [];
        foreach (DurableEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keys is not null && !keys.Contains(entry.Key, StringComparer.Ordinal))
            {
                continue;
            }
            string fingerprint = ContractJson.Hash(entry);
            DurableEntry? changed = entry;
            if (string.Equals(known.GetValueOrDefault(entry.Key), fingerprint, StringComparison.Ordinal))
            {
                changed = null;
            }
            result.Add(new(entry.Key, fingerprint, changed));
        }
        return result;
    }
}
