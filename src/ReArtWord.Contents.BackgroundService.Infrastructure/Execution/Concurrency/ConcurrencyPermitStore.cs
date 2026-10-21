using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Concurrency;

/// <summary>制限区間の現役枠だけを現在行として読み書きします。</summary>
internal sealed class ConcurrencyPermitStore(PeerCluster cluster, IDurableRepository repository)
{
    private static string Scope => "execution:concurrency:permits";

    /// <summary>担当の世代が変わった場合だけ、共有された現在行から占有を復元します。</summary>
    /// <returns>現在値の読取りです。</returns>
    internal async Task RestoreAsync(WorkflowConcurrencyTarget target, ConcurrencyGateQueue queue, CancellationToken cancellationToken = default)
    {
        long epoch = cluster.CoordinatorRevision;
        if (queue.Epoch == epoch)
        {
            return;
        }
        queue.Entry = await repository.ReadAsync(Scope, ContractJson.Hash(target), cancellationToken).ConfigureAwait(false);
        queue.Active.Clear();
        queue.Active.AddRange(queue.Entry?.Value?.Json.Deserialize<ConcurrencyPermit[]>() ?? []);
        queue.Epoch = epoch;
        queue.NextRecovery = DateTimeOffset.MinValue;
    }

    /// <summary>一つの制限ノードの現役記録をCASで変更し、履歴全体を読みません。</summary>
    /// <returns>占有を変更できたことの共有確認です。</returns>
    internal async Task<ClusterResponse> SaveAsync(WorkflowConcurrencyTarget target, ConcurrencyGateQueue queue, ConcurrencyPermit[] permits, Guid operation, CancellationToken cancellationToken = default)
    {
        long epoch = cluster.CoordinatorRevision;
        NodeValue? value = null;
        if (permits.Length != 0)
        {
            value = NodeValue.FromObject(permits);
        }
        DurableReceipt receipt = await cluster.ReplicateAsync(operation, [new(Scope, ContractJson.Hash(target), queue.Entry?.Version, value)], cancellationToken).ConfigureAwait(false);
        queue.Epoch = -1;
        if (receipt.Outcome == EOutcome.Succeeded)
        {
            queue.Entry = receipt.Entries.Single();
            queue.Active.Clear();
            queue.Active.AddRange(permits);
            queue.Epoch = epoch;
            queue.Pulse();
        }
        return new(receipt.Outcome, Reason: receipt.Reason);
    }

}
