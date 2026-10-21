using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Causality;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>準備受理後の同期で進んだ現在値を、確定済み操作の失敗や再適用へ変換しません。</summary>
internal static class PreparedCommit
{
    /// <summary>受理済み本文は保持したが、並行する現在値を確定できていない状態です。</summary>
    internal static string ConflictReason => "Persistence.CommitConflict";

    /// <summary>元準備と結果不明の証拠だけを読取り対象へ足し、通常の保存では配列を作り直しません。</summary>
    /// <returns>一取引で必要な現在値の対象です。</returns>
    internal static IReadOnlyList<DurableMutation> RequiredTargets(IReadOnlyList<DurableMutation> mutations, PreparedBatch? prepared, DurableReceipt? receipt)
    {
        bool proving = receipt?.Outcome == EOutcome.Unknown && string.Equals(receipt.Reason, ConflictReason, StringComparison.Ordinal);
        if (!proving)
        {
            if (prepared is null)
            {
                return mutations;
            }
            if (mutations.Count == 0)
            {
                return prepared.Mutations;
            }
        }
        List<DurableMutation> targets = [.. mutations, .. prepared?.Mutations ?? []];
        if (proving)
        {
            targets.AddRange(receipt!.Entries.Select(entry => new DurableMutation(entry.Scope, entry.Key, null, null)));
        }
        return targets;
    }

    /// <summary>元結果または元予約だけを確定し、準備後に同期した値を新たなCAS失敗にしません。</summary>
    /// <returns>再照会できる元操作の結果です。</returns>
    internal static DurableReceipt Complete(RepositoryDocument document, Guid operationId)
    {
        JsonHistoryAccess.Hydrate(document, operationId, []);
        if (document.Receipts.TryGetValue(operationId, out DurableReceipt? completed))
        {
            if (completed.Outcome == EOutcome.Unknown && string.Equals(completed.Reason, ConflictReason, StringComparison.Ordinal)
                && ReceiptReconciliation.TryReconcile(document, completed with { Outcome = EOutcome.Succeeded, Reason = null }))
            {
                completed = document.Receipts[operationId];
            }
            return RepositoryMerge.ConfirmReservedReceipt(document, completed);
        }
        if (!document.Prepared.Remove(operationId, out PreparedBatch? prepared))
        {
            return new(operationId, string.Empty, EOutcome.Unknown, [], "Persistence.PreparationMissing");
        }
        if (prepared.Mutations.All(mutation => RepositoryMerge.Matches(document, mutation, document.Entries.GetValueOrDefault(DurableRepository.GetAddress(mutation.Scope, mutation.Key)))
            && !DurableRepository.IsReserved(document, mutation)))
        {
            return DurableRepository.Apply(document, operationId, prepared.Mutations.ToArray(), prepared.Fingerprint, completingPrepared: true);
        }
        return MergeAccepted(document, prepared);
    }

    /// <summary>受理済み本文を一度だけ確定し、後続版または並行分岐との関係を通常の同期規則で保持します。</summary>
    /// <returns>現在値で確認した保存成功、または並行分岐を保持した結果不明です。</returns>
    private static DurableReceipt MergeAccepted(RepositoryDocument document, PreparedBatch prepared)
    {
        DurableEntry[] entries = prepared.Mutations.Select(mutation => RepositoryMerge.CreateResolved(prepared.OperationId, mutation)).ToArray();
        RepositoryCausalEntry[] clocks = prepared.Mutations.Where(mutation => mutation.CausalVersion is not null)
            .Select(mutation => new RepositoryCausalEntry(mutation.Scope, mutation.Key, prepared.OperationId.ToString("N"), mutation.CausalVersion!)).ToArray();
        DurableReceipt receipt = new(prepared.OperationId, prepared.Fingerprint, EOutcome.Succeeded, entries);
        document.Receipts.Add(prepared.OperationId, receipt);
        foreach (DurableEntry local in entries.Where(entry => !DurableRepository.IsShared(entry.Scope)))
        {
            document.Entries[DurableRepository.GetAddress(local.Scope, local.Key)] = local;
        }

        // 準備時にCASは受理済みです。同期で先に届いた後続値は維持し、異なる並行値は競合として残します。
        _ = RepositoryMerge.Merge(document, new(entries, [receipt], CausalEntries: clocks));
        if (entries.Any(entry => document.Conflicts.Any(conflict => string.Equals(conflict.Scope, entry.Scope, StringComparison.Ordinal)
            && string.Equals(conflict.Key, entry.Key, StringComparison.Ordinal))))
        {
            receipt = receipt with { Outcome = EOutcome.Unknown, Reason = ConflictReason };
            document.Receipts[prepared.OperationId] = receipt;
        }
        return receipt;
    }
}
