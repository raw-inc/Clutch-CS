using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>診断履歴を小さい固定区間へ分け、確定した索引が参照する区間を上書きしません。</summary>
internal sealed class ResourceHistoryStore(IDurableRepository repository, string scope)
{
    private HistoryIndex? index;
    private Guid lastId;
    private bool legacyCleanupPending;

    /// <summary>確定索引を先に読み、旧形式は初回保存まで読み取り専用で保持します。</summary>
    /// <returns>順序を保持した保存済み履歴です。</returns>
    internal async Task<ResourceSample[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        index = await LocalJsonSnapshots.ReadAsync<HistoryIndex>(repository, scope, "history.index", cancellationToken).ConfigureAwait(false);
        ResourceSample[]? legacy = await LocalJsonSnapshots.ReadAsync<ResourceSample[]>(repository, scope, "history", cancellationToken).ConfigureAwait(false);
        legacyCleanupPending = legacy is { Length: > 0 };
        if (index is null)
        {
            // LEGACY-FORMAT: resource-history-array-v1 — ページ形式の移行完了確認後に旧配列の読取りを撤去します。
            return legacy ?? [];
        }
        List<ResourceSample> samples = [];
        long? next = null;
        foreach (HistoryPage page in index.Pages)
        {
            if (page.Count is < 1 or > 32 || page.Start < 0 || (next.HasValue && next.Value != page.Start))
            {
                throw new InvalidDataException("Resource history index is invalid.");
            }
            ResourceSample[] values = await LocalJsonSnapshots.ReadAsync<ResourceSample[]>(repository, scope, page.Key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Resource history page is missing.");
            if (values.Length != page.Count || values.Any(sample => sample is null))
            {
                throw new InvalidDataException("Resource history page is invalid.");
            }
            samples.AddRange(values);
            next = page.Start + page.Count;
        }
        if (next.HasValue && next.Value != index.Next)
        {
            throw new InvalidDataException("Resource history sequence is invalid.");
        }
        lastId = samples.LastOrDefault()?.Id ?? Guid.Empty;
        return samples.ToArray();
    }

    /// <summary>変更区間を空きスロットへ書き、最後の索引置換で一括確定します。</summary>
    /// <returns>全区間が永続化できた場合だけtrueです。</returns>
    internal async Task<bool> WriteAsync(ResourceSample[] samples, CancellationToken cancellationToken = default)
    {
        int previous = Array.FindLastIndex(samples, sample => sample.Id == lastId);
        long next = checked((index?.Next ?? 0) + samples.Length - previous - 1);
        long first = next - samples.Length;
        List<HistoryPage> pages = [];
        HashSet<string> occupied = (index?.Pages ?? []).Select(page => page.Key).ToHashSet(StringComparer.Ordinal);
        for (int offset = 0; offset < samples.Length;)
        {
            long start = first + offset;
            int count = Math.Min(32 - (int)(start % 32), samples.Length - offset);
            HistoryPage? retained = index?.Pages.FirstOrDefault(page => page.Start == start && page.Count == count);
            if (retained is not null)
            {
                pages.Add(retained);
            }
            else
            {
                int slot = 0;
                string key;
                do
                {
                    key = "history.page." + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    slot++;
                }
                while (!occupied.Add(key));
                ResourceSample[] values = samples.AsSpan(offset, count).ToArray();
                if (await WriteValueAsync(key, values, cancellationToken).ConfigureAwait(false) != EOutcome.Succeeded)
                {
                    return false;
                }
                pages.Add(new(key, start, count));
            }
            offset += count;
        }
        HistoryIndex updated = new(next, pages.ToArray());
        string indexKey = "history.index";
        if (await WriteValueAsync(indexKey, updated, cancellationToken).ConfigureAwait(false) != EOutcome.Succeeded)
        {
            return false;
        }
        index = updated;
        lastId = samples.LastOrDefault()?.Id ?? Guid.Empty;
        if (legacyCleanupPending)
        {
            ResourceSample[] confirmed = await new ResourceHistoryStore(repository, scope).ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ContractJson.Hash(samples), ContractJson.Hash(confirmed), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Resource history migration could not be verified.");
            }
            string legacyKey = "history";
            if (await WriteValueAsync(legacyKey, Array.Empty<ResourceSample>(), cancellationToken).ConfigureAwait(false) != EOutcome.Succeeded)
            {
                return false;
            }
            legacyCleanupPending = false;
        }
        return true;
    }

    /// <summary>共通の局所保存経路を使用し、診断保存で操作履歴を増やしません。</summary>
    /// <returns>媒体が確認した保存結果です。</returns>
    private ValueTask<EOutcome> WriteValueAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        => LocalJsonSnapshots.WriteAsync(repository, scope, key, writer => JsonSerializer.Serialize(writer, value, ContractJson.Options), cancellationToken);

    /// <summary>一度に確定する区間索引です。</summary>
    internal sealed record HistoryIndex(long Next, HistoryPage[] Pages);

    /// <summary>安定した採取順序と永続スロットの対応です。</summary>
    internal sealed record HistoryPage(string Key, long Start, int Count);
}
