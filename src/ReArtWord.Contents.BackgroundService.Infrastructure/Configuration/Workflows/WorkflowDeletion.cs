using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

/// <summary>確認版を保護してから共有の削除印を保存し、古いPeerの定義復活を防ぎます。</summary>
internal sealed class WorkflowDeletion(WorkflowStore store, IDurableRepository repository, PeerCluster cluster, WorkflowActivation activation)
{
    private readonly ConfigurationOperationJournal journal = new(repository);

    /// <summary>削除を再実行せず、元の段階結果だけを読みます。</summary>
    /// <returns>元操作の結果、または未受付です。</returns>
    internal async Task<WorkflowDeleteResult?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ConfigurationOperation? known = await journal.ReadAsync("delete", id, cancellationToken).ConfigureAwait(false);
        if (known?.Result is { } result)
        {
            return result.Deserialize<WorkflowDeleteResult>();
        }
        WorkflowDeleteRequest? original = known?.Context?.Deserialize<WorkflowDeleteRequest>();
        return original is null ? null
                                : Failure(original, EOutcome.Unknown, "Workflow.OperationInterrupted");
    }

    /// <summary>保存・復元と同じ排他境界を使用します。</summary>
    /// <returns>削除の段階結果です。結果不明は同じ操作IDで照会します。</returns>
    internal async Task<WorkflowDeleteResult> DeleteAsync(WorkflowDeleteRequest request, bool allowed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedRevision);
        ArgumentOutOfRangeException.ThrowIfEqual(request.OperationId, Guid.Empty);
        if (!allowed || !store.TryReserve(request.WorkflowId, request.OperationId))
        {
            return Failure(request, EOutcome.NotExecuted, "Workflow.DeleteUnavailable");
        }
        try
        {
            ConfigurationOperation? known = await journal.ReadAsync("delete", request.OperationId, cancellationToken).ConfigureAwait(false);
            if (known is not null)
            {
                if (!string.Equals(known.Fingerprint, ContractJson.Hash(request), StringComparison.Ordinal))
                {
                    return Failure(request, EOutcome.Failed, "Workflow.OperationMismatch");
                }
                return known.Result?.Deserialize<WorkflowDeleteResult>() ?? Failure(request, EOutcome.Unknown, "Workflow.OperationInterrupted");
            }
            string operationKind = "delete";
            if (!await journal.BeginAsync(operationKind, request.OperationId, request, JsonSerializer.SerializeToElement(request), cancellationToken).ConfigureAwait(false))
            {
                return Failure(request, EOutcome.Unknown, "Workflow.StartUnconfirmed");
            }
            WorkflowDeleteResult result = await DeleteCoreAsync(request, cancellationToken).ConfigureAwait(false);
            await journal.CompleteAsync("delete", request.OperationId, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException or OperationCanceledException)
        {
            return Failure(request, EOutcome.Unknown, "Workflow.DeleteUnconfirmed");
        }
        finally
        {
            store.ReleaseReservation(request.WorkflowId, request.OperationId);
        }
    }

    /// <summary>保存版と有効化希望を同一の共有CASで削除します。</summary>
    /// <returns>共有済みと各Peerの実停止完了を混同しない結果です。</returns>
    private async Task<WorkflowDeleteResult> DeleteCoreAsync(WorkflowDeleteRequest request, CancellationToken cancellationToken = default)
    {
        DurableEntry? current = await repository.ReadAsync("workflows", request.WorkflowId, cancellationToken).ConfigureAwait(false);
        StoredWorkflow? saved = current?.Value?.Json.Deserialize<StoredWorkflow>();
        if (saved is null || !string.Equals(saved.Revision, request.ExpectedRevision, StringComparison.Ordinal))
        {
            return Failure(request, EOutcome.NotExecuted, "Workflow.RevisionConflict");
        }
        DurableEntry? active = await repository.ReadAsync("workflow.activation", request.WorkflowId, cancellationToken).ConfigureAwait(false);
        DurableMutation? guard = await new SnapshotEditProtection(repository, cluster).GuardAsync(request.WorkflowId, saved.Definition, null, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (guard is null)
        {
            return Failure(request, EOutcome.NotExecuted, "Workflow.RestoreLocked");
        }
        Guid snapshotId = WorkflowStore.PhaseId(request.OperationId, "delete-snapshot");
        NodeValue state = NodeValue.FromObject(new ConfigurationSnapshotState(saved, store.GetEffective?.Invoke(request.WorkflowId)?.Definition));
        SnapshotRecord snapshot = new(snapshotId.ToString("N"), request.WorkflowId, cluster.LocalPeerId, ESnapshotKind.Configuration, DateTimeOffset.UtcNow, state, ContractJson.Hash(state));
        ConfigurationPhaseResult captured = await new SnapshotCatalog(repository, cluster).SaveAsync(snapshot, snapshotId, cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult idle = new(EOutcome.NotExecuted);
        WorkflowDeleteResult result = new(request.OperationId, request.WorkflowId, captured, idle, idle, snapshot.Id);
        if (captured.Outcome != EOutcome.Succeeded)
        {
            return result;
        }
        await journal.CompleteAsync("delete", request.OperationId, result with { Deleted = new(EOutcome.Unknown, "Workflow.DeleteInProgress") }, cancellationToken).ConfigureAwait(false);
        DurableReceipt deleted = await cluster.ReplicateAsync(request.OperationId,
            [new DurableMutation("workflows", request.WorkflowId, current!.Version, null), new DurableMutation("workflow.activation", request.WorkflowId, active?.Version, null), guard], cancellationToken).ConfigureAwait(false);
        result = result with { Deleted = new(deleted.Outcome, deleted.Reason) };
        if (deleted.Outcome != EOutcome.Succeeded)
        {
            return result;
        }
        await journal.CompleteAsync("delete", request.OperationId, result with { Deactivation = new(EOutcome.Unknown, "Activation.InProgress") }, cancellationToken).ConfigureAwait(false);
        return result with { Deactivation = await activation.DeactivateLocalAsync(request.OperationId, request.WorkflowId, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>開始不能と結果不明の理由を保持します。</summary>
    /// <returns>未確認の段階を成功にしない結果です。</returns>
    private static WorkflowDeleteResult Failure(WorkflowDeleteRequest request, EOutcome outcome, string reason)
    {
        ConfigurationPhaseResult phase = new(outcome, reason);
        return new(request.OperationId, request.WorkflowId, phase, phase, phase);
    }
}
