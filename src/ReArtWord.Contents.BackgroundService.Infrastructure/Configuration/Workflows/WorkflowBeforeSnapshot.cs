using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

/// <summary>設定変更前の保護を通常保存・離脱保存で共用します。</summary>
internal sealed class WorkflowBeforeSnapshot(IDurableRepository repository, PeerCluster cluster)
{
    /// <summary>保存不能時の候補利用も、変更前の保護が成立した操作だけに限定します。</summary>
    /// <returns>保護不要、または同じ旧版の共有保存を確認できた場合だけtrueです。</returns>
    internal async Task<bool> IsProtectedAsync(Guid operationId, StoredWorkflow? before, WorkflowDefinition after, CancellationToken cancellationToken = default)
    {
        if (before is null || WorkflowChangeInspection.ChangedNodes(before.Definition, after).Count == 0)
        {
            return true;
        }
        Guid id = WorkflowStore.PhaseId(operationId, "before-node-settings");
        DurableReceipt? receipt = await cluster.GetOperationAsync(id, cancellationToken).ConfigureAwait(false);
        if (receipt?.Outcome != EOutcome.Succeeded)
        {
            return false;
        }
        SnapshotRecord? snapshot = await new SnapshotCatalog(repository, cluster).ReadAsync(id.ToString("N"), cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return false;
        }
        ConfigurationSnapshotState? state = System.Text.Json.JsonSerializer.Deserialize<ConfigurationSnapshotState>(snapshot.Value.Json);
        return state is not null && string.Equals(state.Saved.Revision, before.Revision, StringComparison.Ordinal)
            && string.Equals(snapshot.TargetId, before.Definition.Id, StringComparison.Ordinal);
    }

    /// <summary>旧保存版と旧有効版を取得し、確認できた保護結果だけを返します。</summary>
    /// <returns>変更不要ならnull、それ以外は取得結果です。</returns>
    internal async Task<SnapshotCaptureResult?> CaptureAsync(Guid operationId, StoredWorkflow? before, WorkflowDefinition after,
        WorkflowDefinition? effective, bool localOnly, CancellationToken cancellationToken = default)
    {
        if (before is null || WorkflowChangeInspection.ChangedNodes(before.Definition, after).Count == 0)
        {
            return null;
        }
        Guid id = WorkflowStore.PhaseId(operationId, "before-node-settings");
        NodeValue value = NodeValue.FromObject(new ConfigurationSnapshotState(before, effective));
        SnapshotRecord snapshot = new(id.ToString("N"), before.Definition.Id, cluster.LocalPeerId, ESnapshotKind.Configuration,
            DateTimeOffset.UtcNow, value, ContractJson.Hash(value), LocalOnly: localOnly);
        try
        {
            ConfigurationPhaseResult result;
            if (localOnly)
            {
                DurableReceipt receipt = await repository.ApplyBatchAsync(id,
                    [new("peer:" + cluster.LocalPeerId + ":detached-snapshots", snapshot.Id, null, NodeValue.FromObject(snapshot))], cancellationToken).ConfigureAwait(false);
                result = new(receipt.Outcome, receipt.Reason);
            }
            else
            {
                result = await new SnapshotCatalog(repository, cluster).SaveAsync(snapshot, id, cancellationToken).ConfigureAwait(false);
            }
            return new(result, snapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException or OperationCanceledException)
        {
            // 保護の確認不能を通常保存の候補利用へ変換せず、元の保存を開始しません。
            return new(new(EOutcome.Unknown, "Snapshot.BeforeChangeUnconfirmed"), snapshot);
        }
    }
}
