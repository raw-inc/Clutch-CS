using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

/// <summary>現在共有状態と、小さい履歴境界を一つの固定版へまとめます。</summary>
internal static class RepositoryExport
{
    /// <summary>すでに本文の同期を終えた一回限りの履歴を再送しません。</summary>
    /// <returns>JSON保存先の完全な共有確認内容です。</returns>
    internal static RepositorySnapshot Create(RepositoryDocument document, bool includeReceipts = true)
    {
        DurableEntry[] entries = document.Entries.Values.Concat(document.Conflicts).Where(entry => DurableRepository.IsShared(entry.Scope)
            && document.History?.RetiredKeys.Contains(DurableRepository.GetAddress(entry.Scope, entry.Key)) != true).Distinct().ToArray();
        IReadOnlyList<DurableReceipt> receipts = [];
        if (includeReceipts)
        {
            receipts = document.Receipts.Values.Where(receipt => receipt.Entries.All(entry => DurableRepository.IsShared(entry.Scope))).ToArray();
        }
        return new(entries, receipts,
            CausalEntries: RepositoryCausality.Export(document, entries), LegacyEntries: RepositoryLegacyAncestry.Export(document, entries),
            HistoryBoundary: RepositoryHistoryGuard.Read(document), RetiredOperations: document.History?.RetiredOperations.Values.Where(operation => operation.Shared).ToArray(),
            ProtectedHistory: entries.Where(entry => entry.Value?.Json.ValueKind == JsonValueKind.Object && HistoryRetentionPolicy.Category("entry", entry.Scope) is { } category
                && JsonHistoryEligibility.Protected(document, entry, category)).Select(entry => DurableRepository.GetAddress(entry.Scope, entry.Key) + entry.Version).ToArray(),
            RetiredKeys: RepositoryHistoryGuard.ExportKeys(document),
            PendingOperations: document.Prepared.Values.Where(batch => batch.Mutations.Any(mutation => DurableRepository.IsShared(mutation.Scope))).Select(batch => batch.OperationId).ToArray());
    }
}
