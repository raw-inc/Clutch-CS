using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>新しい統合保存版にもスナップショット成功後だけの既定有効化を適用します。</summary>
internal static class ConflictSavedSnapshot
{
    /// <summary>共有保存と別のスナップショット結果を確認し、失敗時は有効化を保留します。</summary>
    /// <returns>スナップショットとローカル適用の個別結果です。</returns>
    internal static async Task<(ConfigurationPhaseResult Snapshot, ConfigurationPhaseResult Activation)> CompleteAsync(Guid operationId, StateConflict conflict,
        NodeValue? value, PeerCluster cluster, WorkflowStore workflows, ClusterConfigurationStore operational, IReadOnlyList<PeerInfo> members, CancellationToken cancellationToken = default)
    {
        ConfigurationPhaseResult unstarted = new(EOutcome.NotExecuted);
        NodeValue? state = null;
        StoredWorkflow? definition = null;
        ClusterConfigurationStored? common = null;
        string workflowScope = "workflows";
        string commonScope = "cluster.operational";
        if (string.Equals(conflict.Scope, workflowScope, StringComparison.Ordinal) && value is not null)
        {
            definition = value.Json.Deserialize<StoredWorkflow>()!;
            state = NodeValue.FromObject(new ConfigurationSnapshotState(definition, workflows.GetEffective?.Invoke(conflict.Key)?.Definition));
        }
        if (string.Equals(conflict.Scope, commonScope, StringComparison.Ordinal) && value is not null)
        {
            common = value.Json.Deserialize<ClusterConfigurationStored>()!;
            state = NodeValue.FromObject(new ClusterOperationalState(common.Snapshot, operational.Capture(), common.ActivationPolicy));
        }
        if (state is null)
        {
            return (unstarted, unstarted);
        }
        Guid snapshotId = WorkflowStore.PhaseId(operationId, "snapshot");
        SnapshotRecord snapshot = new(snapshotId.ToString("N"), conflict.Key, cluster.LocalPeerId, ESnapshotKind.Configuration, DateTimeOffset.UtcNow, state, ContractJson.Hash(state));
        DurableReceipt saved = await cluster.ReplicateFixedAsync(snapshotId, [new("snapshots", snapshot.Id, null, NodeValue.FromObject(snapshot))], members, cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult captured = new(saved.Outcome, saved.Reason);
        if (saved.Outcome != EOutcome.Succeeded)
        {
            return (captured, unstarted);
        }
        if (definition?.ActivationPolicy == EActivationPolicy.Auto)
        {
            return (captured, await workflows.ActivateAsync(new(WorkflowStore.PhaseId(operationId, "activate"), conflict.Key, definition.Revision), cancellationToken).ConfigureAwait(false));
        }
        if (common?.ActivationPolicy == EActivationPolicy.Auto)
        {
            return (captured, await operational.ActivateAsync(new(WorkflowStore.PhaseId(operationId, "activate"), common.Snapshot.Revision), cancellationToken).ConfigureAwait(false));
        }
        return (captured, unstarted);
    }
}
