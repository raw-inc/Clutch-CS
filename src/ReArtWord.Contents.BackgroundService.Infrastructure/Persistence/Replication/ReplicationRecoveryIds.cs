using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

/// <summary>準備の終了後も残る新形式の確認待ちを、JSON保存の巡回へ含めます。</summary>
internal static class ReplicationRecoveryIds
{
    /// <summary>保持済みの終端履歴や旧形式の曖昧な判断を、新しい取消判断に読み替えません。</summary>
    /// <returns>新しい判断と現存する共有準備の有限な集合です。</returns>
    internal static IReadOnlyList<Guid> Read(RepositoryDocument document, Guid after, int maximum, bool recovery, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, 64);
        cancellationToken.ThrowIfCancellationRequested();
        string scope = "cluster:operations";
        IEnumerable<Guid> pending = document.Entries.Values.Where(entry => recovery && string.Equals(entry.Scope, scope, StringComparison.Ordinal))
            .Select(entry => entry.Value?.Json.Deserialize<DurableReceipt>()).OfType<DurableReceipt>()
            .Where(receipt => receipt.Outcome == EOutcome.Unknown && (string.Equals(receipt.Reason, ReplicationJournal.Preparing, StringComparison.Ordinal)
                || string.Equals(receipt.Reason, ReplicationJournal.CommitDecided, StringComparison.Ordinal) || string.Equals(receipt.Reason, ReplicationJournal.AbortDecided, StringComparison.Ordinal)))
            .Select(receipt => receipt.OperationId);
        return document.Prepared.Values.Where(batch => batch.Mutations.Any(mutation => DurableRepository.IsShared(mutation.Scope))).Select(batch => batch.OperationId)
            .Concat(pending).Where(id => 0 < id.CompareTo(after)).Distinct().Order().Take(maximum).ToArray();
    }
}
