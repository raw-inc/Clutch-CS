using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>読み取った共有値を、その値を確定した操作の記録と照合します。</summary>
internal sealed class ReplicatedEntryConfirmation(PeerCluster cluster, IDurableRepository repository)
{
    /// <summary>操作元では全員の確認結果を、受信先では取込済みの同じ確定結果を確認します。</summary>
    /// <returns>指定内容を使用できる保存証拠があるか。競合解消で版だけが変わった場合も内容を照合します。</returns>
    internal async ValueTask<bool> CheckAsync(DurableEntry expected, Guid operationId, IReadOnlyList<PeerInfo> members, bool local,
        CancellationToken cancellationToken = default)
    {
        DurableReceipt? receipt = null;
        if (!local)
        {
            receipt = await cluster.GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (receipt?.Outcome == EOutcome.Unknown)
            {
                receipt = await cluster.ConfirmReplicationAsync(operationId, members, cancellationToken).ConfigureAwait(false);
            }
            if (receipt is not null && receipt.Outcome != EOutcome.Succeeded)
            {
                return false;
            }
        }
        receipt ??= await repository.ReadReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        return receipt?.Outcome == EOutcome.Succeeded && receipt.Entries.Any(entry => string.Equals(entry.Scope, expected.Scope, StringComparison.Ordinal)
            && string.Equals(entry.Key, expected.Key, StringComparison.Ordinal)
            && string.Equals(ContractJson.Hash(entry.Value), ContractJson.Hash(expected.Value), StringComparison.Ordinal));
    }
}
