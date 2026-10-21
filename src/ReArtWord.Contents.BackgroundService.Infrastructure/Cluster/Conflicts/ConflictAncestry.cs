using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>保存された親関係だけから三者比較の元版を確認します。</summary>
internal static class ConflictAncestry
{
    /// <summary>全候補の共通祖先を確認し、欠損履歴から元値を推測しません。</summary>
    /// <returns>確認できた共通元版です。</returns>
    internal static async Task<DurableEntry?> FindAsync(IReadOnlyList<DurableEntry> candidates, IDurableRepository repository, CancellationToken cancellationToken = default)
    {
        List<Dictionary<string, DurableEntry>> ancestors = [];
        foreach (DurableEntry candidate in candidates)
        {
            Dictionary<string, DurableEntry> entries = new(StringComparer.Ordinal);
            Queue<DurableEntry> pending = new([candidate]);
            while (pending.TryDequeue(out DurableEntry? entry))
            {
                if (!entries.TryAdd(entry.Version, entry))
                {
                    continue;
                }
                if (4096 < entries.Count)
                {
                    return null;
                }
                IEnumerable<string?> parents = (entry.MergedVersions ?? []).Cast<string?>().Append(entry.ParentVersion);
                foreach (string? parent in parents.Distinct(StringComparer.Ordinal))
                {
                    if (parent is null || entries.ContainsKey(parent) || !Guid.TryParse(parent, out Guid operation))
                    {
                        continue;
                    }
                    DurableReceipt? receipt = await repository.ReadReceiptAsync(operation, cancellationToken).ConfigureAwait(false);
                    DurableEntry? value = receipt?.Entries.FirstOrDefault(item => string.Equals(item.Scope, entry.Scope, StringComparison.Ordinal) && string.Equals(item.Key, entry.Key, StringComparison.Ordinal) && string.Equals(item.Version, parent, StringComparison.Ordinal));
                    if (value is not null)
                    {
                        pending.Enqueue(value);
                    }
                }
            }
            ancestors.Add(entries);
        }
        return ancestors[0].Values.FirstOrDefault(entry => ancestors.All(values => values.ContainsKey(entry.Version)));
    }
}
