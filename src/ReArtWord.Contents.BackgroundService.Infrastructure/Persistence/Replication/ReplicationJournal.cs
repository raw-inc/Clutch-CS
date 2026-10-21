using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

/// <summary>クラスタ全体の確定判断を、個別Peerの保存結果と区別して耐久保存します。</summary>
internal sealed class ReplicationJournal(IDurableRepository repository)
{
    /// <summary>新しい複製では、コミット決定前であることを明示して保存します。</summary>
    internal static string Preparing => "Replication.Preparing";

    /// <summary>最初のコミット送信より前に保存する、取消へ戻せない確定判断です。</summary>
    internal static string CommitDecided => "Replication.CommitDecided";

    /// <summary>コミットを送信していない操作の中止判断です。</summary>
    internal static string AbortDecided => "Replication.AbortDecided";

    /// <summary>内部の耐久判断を、既存の公開結果と確認不能理由へ投影します。</summary>
    /// <returns>判断を失わず外部契約を維持した照会結果です。</returns>
    internal static DurableReceipt? PublicResult(DurableReceipt? receipt)
    {
        if (receipt?.Outcome == EOutcome.Unknown && string.Equals(receipt.Reason, Preparing, StringComparison.Ordinal))
        {
            return receipt with { Reason = "Replication.InProgress" };
        }
        if (receipt?.Outcome == EOutcome.Unknown && (string.Equals(receipt.Reason, CommitDecided, StringComparison.Ordinal) || string.Equals(receipt.Reason, AbortDecided, StringComparison.Ordinal)))
        {
            return receipt with { Reason = "Replication.RequiredAcknowledgementMissing" };
        }
        return receipt;
    }

    /// <summary>単一媒体では元コミットと判断を一括保存し、その他は判断を先に耐久保存します。</summary>
    /// <returns>遠隔コミットを送信する前に確認した局所結果です。</returns>
    internal async Task<DurableReceipt> CommitAsync(DurableReceipt decision, CancellationToken cancellationToken = default)
    {
        string scope = "cluster:operations", key = decision.OperationId.ToString("N");
        DurableEntry? current = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        ReplicationCommit commit = new(decision.OperationId, new(scope, key, current?.Version, NodeValue.FromObject(decision)));
        if (repository is IReplicationTransactionStore store && await store.TryCommitAsync(commit, cancellationToken).ConfigureAwait(false) is { } committed)
        {
            return committed;
        }
        await SaveAsync(decision, cancellationToken).ConfigureAwait(false);
        return await repository.CommitAsync(decision.OperationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>局所保存の成功を、固定した全参加者の成功へ読み替えません。</summary>
    /// <returns>元の共有結果、または明示失効した結果です。</returns>
    internal async ValueTask<DurableReceipt?> ReadAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("cluster:operations", operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        DurableReceipt? result = entry?.Value?.Json.Deserialize<DurableReceipt>();
        if (result is null)
        {
            DurableReceipt? retired = await repository.ReadReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (string.Equals(retired?.Reason, RepositoryHistoryGuard.ExpiredReason, StringComparison.Ordinal))
            {
                return retired;
            }
        }
        return result;
    }

    /// <summary>同じ操作の確認が先行した場合だけ成功を共有し、別結果の上書きは拒否します。</summary>
    /// <returns>共有判断の永続確認です。</returns>
    internal async Task SaveAsync(DurableReceipt receipt, CancellationToken cancellationToken = default)
    {
        string key = receipt.OperationId.ToString("N"), scope = "cluster:operations";
        DurableEntry? current = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        DurableReceipt saved = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(scope, key, current?.Version, NodeValue.FromObject(receipt))], cancellationToken).ConfigureAwait(false);
        if (saved.Outcome == EOutcome.Succeeded)
        {
            return;
        }
        string versionConflict = "Persistence.VersionConflict";
        if (saved.Outcome == EOutcome.Failed && string.Equals(saved.Reason, versionConflict, StringComparison.Ordinal))
        {
            DurableEntry? latest = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            DurableReceipt? confirmed = latest?.Value?.Json.Deserialize<DurableReceipt>();
            if (confirmed is not null && string.Equals(ContractJson.Hash(confirmed), ContractJson.Hash(receipt), StringComparison.Ordinal)
                && !(await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false)).Any(entry => string.Equals(entry.Key, key, StringComparison.Ordinal)))
            {
                return;
            }
        }
        throw new InvalidDataException("共有操作の確認結果を保存できません。");
    }
}
