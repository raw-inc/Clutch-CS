using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>専用読取りを持たない媒体でも、通常の検証済み列挙から同じ観測を作ります。</summary>
internal static class RepositoryObservations
{
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
