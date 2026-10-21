using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;

/// <summary>共通設定の保存・共有・スナップショットを段階ごとに確認します。</summary>
internal sealed class ClusterConfigurationSaving(IDurableRepository repository, PeerCluster cluster, SnapshotCatalog snapshots, ConfigurationOperationJournal journal, SnapshotEditProtection protection, ClusterConfigurationAvailability availability, Func<ClusterOperationalSnapshot?> capture, Func<ClusterOperationalActivateRequest, CancellationToken, Task<ConfigurationPhaseResult>> activate)
{
    /// <summary>保存候補を期待版で比較し、既存スナップショットカタログへ同じ段階規則で格納します。</summary>
    /// <returns>確認できた各段階です。</returns>
    internal async Task<ClusterOperationalSaveResult> SaveAsync(ClusterOperationalSaveRequest request, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ValidationIssue> issues = ClusterConfigurationValidation.Validate(request.Settings);
        ConfigurationPhaseResult none = new(EOutcome.NotExecuted);
        if (issues.Count != 0 || request.OperationId == Guid.Empty || !Enum.IsDefined(request.ActivationPolicy))
        {
            return new(request.OperationId, null, issues, none, none, none, none);
        }
        DurableEntry? current = await repository.ReadAsync(ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false);
        ClusterConfigurationStored? before = current?.Value?.Json.Deserialize<ClusterConfigurationStored>();
        if (!string.Equals(before?.Snapshot.Revision, request.ExpectedRevision, StringComparison.Ordinal))
        {
            return ClusterConfigurationValidation.Unstarted(request.OperationId, EOutcome.Failed, "ClusterConfiguration.RevisionConflict");
        }
        ClusterOperationalSnapshot candidate = new(request.Settings, ContractJson.Hash(request.Settings), DateTimeOffset.UtcNow);
        ClusterConfigurationStored stored = new(candidate, request.ActivationPolicy);
        List<DurableMutation> mutations = [new(ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, current?.Version, NodeValue.FromObject(stored))];
        DurableMutation? guard = await protection.GuardConfigurationAsync(ClusterConfigurationStore.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (guard is null)
        {
            return ClusterConfigurationValidation.Unstarted(request.OperationId, EOutcome.NotExecuted, "ClusterConfiguration.RestoreLocked");
        }
        mutations.Add(guard);
        if (await repository.ReadAsync(ClusterConfigurationStore.HistoryScope, candidate.Revision, cancellationToken).ConfigureAwait(false) is null)
        {
            mutations.Add(new(ClusterConfigurationStore.HistoryScope, candidate.Revision, null, NodeValue.FromObject(stored)));
        }
        DurableReceipt shared = await cluster.ReplicateAsync(request.OperationId, mutations, cancellationToken).ConfigureAwait(false);
        ClusterConfigurationStored? actual = await ReadStoredAsync(cancellationToken).ConfigureAwait(false);
        if (actual is not null)
        {
            await availability.RememberSavedAsync(actual, cancellationToken).ConfigureAwait(false);
        }
        ConfigurationPhaseResult local = new(shared.Outcome, shared.Reason);
        if (actual is not null && string.Equals(ContractJson.Hash(actual), ContractJson.Hash(stored), StringComparison.Ordinal))
        {
            local = new(EOutcome.Succeeded);
        }
        ClusterOperationalSaveResult result = new(request.OperationId, candidate.Revision, [], local, new(shared.Outcome, shared.Reason), none, none);
        await journal.CompleteAsync(ClusterConfigurationStore.SaveKind, request.OperationId, result, cancellationToken).ConfigureAwait(false);
        if (shared.Outcome != EOutcome.Succeeded)
        {
            return result;
        }
        Guid snapshotId = WorkflowStore.PhaseId(request.OperationId, "snapshot");
        NodeValue value = NodeValue.FromObject(new ClusterOperationalState(candidate, capture(), request.ActivationPolicy));
        SnapshotRecord snapshot = new(snapshotId.ToString("N"), ClusterConfigurationStore.Id, cluster.LocalPeerId, ESnapshotKind.Configuration, candidate.SavedAt, value, ContractJson.Hash(value));
        await journal.CompleteAsync(ClusterConfigurationStore.SaveKind, request.OperationId, result with { Snapshot = new(EOutcome.Unknown, "Snapshot.InProgress") }, cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult captured = await snapshots.SaveAsync(snapshot, snapshotId, cancellationToken).ConfigureAwait(false);
        result = result with { Snapshot = captured };
        await journal.CompleteAsync(ClusterConfigurationStore.SaveKind, request.OperationId, result, cancellationToken).ConfigureAwait(false);
        if (captured.Outcome == EOutcome.Succeeded && request.ActivationPolicy == EActivationPolicy.Auto)
        {
            await journal.CompleteAsync(ClusterConfigurationStore.SaveKind, request.OperationId, result with { Activation = new(EOutcome.Unknown, "Activation.InProgress") }, cancellationToken).ConfigureAwait(false);
            ConfigurationPhaseResult activation = await activate(new(WorkflowStore.PhaseId(request.OperationId, "activate"), candidate.Revision), cancellationToken).ConfigureAwait(false);
            result = result with { Activation = activation };
        }
        return result;
    }

    /// <summary>primary保存版を候補と区別して読みます。</summary>
    /// <returns>実保存した版です。</returns>
    private async Task<ClusterConfigurationStored?> ReadStoredAsync(CancellationToken cancellationToken = default) => (await repository.ReadAsync(ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<ClusterConfigurationStored>();
}
