using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

/// <summary>最初の遠隔コミット送信前に、局所コミットと判断を同じ取引へ固定します。</summary>
/// <param name="OperationId">元の準備操作です。</param>
/// <param name="Decision">元操作の局所判断の比較更新です。</param>
internal sealed record ReplicationCommit(Guid OperationId, DurableMutation Decision)
{
    /// <summary>準備を消費する前に判断の期待版を確認し、片方だけを保存しません。</summary>
    /// <returns>受理した局所操作の結果です。</returns>
    internal DurableReceipt Apply(RepositoryDocument document)
    {
        string scope = "cluster:operations", key = OperationId.ToString("N");
        if (!string.Equals(Decision.Scope, scope, StringComparison.Ordinal) || !string.Equals(Decision.Key, key, StringComparison.Ordinal) || Decision.Value is null)
        {
            throw new InvalidDataException("元操作の確定判断が不正です。");
        }
        string address = DurableRepository.GetAddress(scope, key);
        DurableEntry? previous = document.Entries.GetValueOrDefault(address);
        if (!RepositoryMerge.Matches(document, Decision, previous))
        {
            throw new InvalidDataException("確定判断の保存版が変わりました。");
        }
        DurableReceipt result = PreparedCommit.Complete(document, OperationId);
        if (result.Outcome == EOutcome.Succeeded || result.Outcome == EOutcome.Unknown && string.Equals(result.Reason, PreparedCommit.ConflictReason, StringComparison.Ordinal))
        {
            // 操作IDの結果照会対象は元コミットです。内部判断の更新ごとに別の操作履歴を増やしません。
            document.Entries[address] = new(scope, key, Guid.CreateVersion7().ToString("N"), Decision.Value, previous?.Version);
        }
        return result;
    }
}
