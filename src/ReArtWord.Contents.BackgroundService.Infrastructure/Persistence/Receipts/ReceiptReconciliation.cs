using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

// LEGACY-FORMAT: prepared-commit-outcome-v1 — 準備後の同期で確定失敗を残した旧版の移行完了後に、Failed補正の互換処理を撤去します。
/// <summary>元操作と現在状態で証明できる保存成功だけを補完し、取消や別内容の結果を上書きしません。</summary>
internal static class ReceiptReconciliation
{
    /// <summary>元の局所結果を、通常の操作履歴保持で確認できる保存範囲です。</summary>
    internal static string AuditScope => "peer:receipt-reconciliation";

    /// <summary>既知の旧確定失敗または未確認結果と、同一内容の成功だけを候補にします。</summary>
    /// <returns>現在値による追加証明の対象かです。</returns>
    internal static bool CanReconcile(DurableReceipt? previous, DurableReceipt incoming)
        => previous is not null && previous.OperationId == incoming.OperationId
            && string.Equals(previous.Fingerprint, incoming.Fingerprint, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(incoming.Fingerprint) && incoming.Outcome == EOutcome.Succeeded && incoming.Entries.Count != 0
            && !string.Equals(previous.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal)
            && (previous.Outcome == EOutcome.Unknown || previous.Outcome == EOutcome.Failed && previous.Entries.Count == 0
                && string.Equals(previous.Reason, "Persistence.VersionConflict", StringComparison.Ordinal));

    /// <summary>既に現在値またはその祖先へ到達した元操作だけを確認し、業務値は再適用しません。</summary>
    /// <returns>前結果を保全して元の保存結果を補完できたかです。</returns>
    internal static bool TryReconcile(RepositoryDocument document, DurableReceipt incoming)
    {
        DurableReceipt? previous = document.Receipts.GetValueOrDefault(incoming.OperationId);
        if (!CanReconcile(previous, incoming)
            || RepositoryHistoryGuard.Expired(incoming.OperationId, RepositoryHistoryGuard.Read(document), document.History?.RetiredOperations.GetValueOrDefault(incoming.OperationId)) is not null)
        {
            return false;
        }
        document.Receipts[incoming.OperationId] = incoming;
        bool verified = incoming.Entries.All(entry => DurableRepository.IsShared(entry.Scope)
            && document.Entries.TryGetValue(DurableRepository.GetAddress(entry.Scope, entry.Key), out DurableEntry? current)
            && !document.Conflicts.Any(conflict => string.Equals(conflict.Scope, entry.Scope, StringComparison.Ordinal) && string.Equals(conflict.Key, entry.Key, StringComparison.Ordinal))
            && RepositoryMerge.IsAncestor(document, entry, current));
        if (!verified)
        {
            document.Receipts[incoming.OperationId] = previous!;
            return false;
        }
        string key = incoming.OperationId.ToString("N");
        NodeValue evidence = NodeValue.FromObject(new { Original = previous! with { Entries = [] }, OriginalHash = ContractJson.Hash(previous), ConfirmedHash = ContractJson.Hash(incoming), Time = DateTimeOffset.UtcNow });
        document.Entries[DurableRepository.GetAddress(AuditScope, key)] = new(AuditScope, key, key, evidence);
        return true;
    }
}
