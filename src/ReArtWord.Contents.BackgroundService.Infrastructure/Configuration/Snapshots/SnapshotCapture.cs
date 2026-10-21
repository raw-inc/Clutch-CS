using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>取得操作の一回性と保存結果を保管します。</summary>
internal sealed class SnapshotCapture(IDurableRepository repository, PeerCluster cluster)
{
    /// <summary>実際に開始した取得操作と確認できた結果の通知先。</summary>
    internal IWorkflowEvents? Events
    {
        get; set;
    }

    /// <summary>取得を再実行せず、操作操作履歴だけを照会します。</summary>
    /// <returns>保存済み結果、開始中のUnknown、未受付ならnull。</returns>
    public async Task<SnapshotCaptureResult?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        ConfigurationOperation? operation = await new ConfigurationOperationJournal(repository).ReadAsync("capture", operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return null;
        }
        return operation.Result?.Deserialize<SnapshotCaptureResult>() ?? new SnapshotCaptureResult(new ConfigurationPhaseResult(EOutcome.Unknown, "Snapshot.CaptureInProgress"), null);
    }

    /// <summary>再送は取得済みの結果を返します。</summary>
    /// <returns>実取得と共有保存の結果。</returns>
    public async Task<SnapshotCaptureResult> CaptureAsync(Guid operationId, SnapshotTarget target, bool permanent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        target = ContractJson.Copy(target);
        object identity = target;
        if (permanent)
        {
            identity = new { target, permanent };
        }
        if (!SnapshotNodeSelection.Valid(target))
        {
            return new SnapshotCaptureResult(new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.InvalidNodeSelection"), null);
        }
        ConfigurationOperationJournal journal = new(repository);
        ConfigurationOperation? known = await journal.ReadAsync("capture", operationId, cancellationToken).ConfigureAwait(false);
        if (known is not null)
        {
            if (!string.Equals(known.Fingerprint, ContractJson.Hash(identity), StringComparison.Ordinal))
            {
                return new SnapshotCaptureResult(new ConfigurationPhaseResult(EOutcome.Failed, "Snapshot.OperationMismatch"), null);
            }
            return known.Result?.Deserialize<SnapshotCaptureResult>() ?? new SnapshotCaptureResult(new ConfigurationPhaseResult(EOutcome.Unknown, "Snapshot.CaptureInterrupted"), null);
        }
        PeerInfo? peer = cluster.GetPeers().FirstOrDefault(item => string.Equals(item.PeerId, target.PeerId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(item.Status));
        if (peer is null)
        {
            return new SnapshotCaptureResult(new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.PeerUnavailable"), null);
        }
        string kind = "capture";
        if (!await journal.BeginAsync(kind, operationId, identity, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new SnapshotCaptureResult(new ConfigurationPhaseResult(EOutcome.Unknown, "Snapshot.StartUnconfirmed"), null);
        }
        SnapshotRequestSender sender = new(repository, cluster);
        await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(WorkflowStore.PhaseId(operationId, "hook.capture.started"), "snapshot.capture.started", DateTimeOffset.UtcNow,
            cluster.LocalPeerId, ContractJson.ToValue(new { operationId, target }), CausationId: operationId), CancellationToken.None).ConfigureAwait(false);
        ClusterResponse response = await sender.SendAsync(kind, kind, operationId, target, peer, null, cancellationToken).ConfigureAwait(false);
        SnapshotCaptureResult result = new(new ConfigurationPhaseResult(response.Outcome, response.Reason), null);
        if (response.Outcome == EOutcome.Succeeded && response.Value is not null)
        {
            SnapshotRecord snapshot = new(operationId.ToString("N"), target.Id, target.PeerId, target.Kind, DateTimeOffset.UtcNow, response.Value.Copy(), ContractJson.Hash(response.Value), target.NodeIds, Permanent: permanent);
            SnapshotCatalog catalog = new(repository, cluster);
            ConfigurationPhaseResult saved = await catalog.SaveAsync(snapshot, WorkflowStore.PhaseId(operationId, "capture-save"), cancellationToken).ConfigureAwait(false);
            result = new SnapshotCaptureResult(saved, snapshot);
        }
        await journal.CompleteAsync(kind, operationId, result, CancellationToken.None).ConfigureAwait(false);
        await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(WorkflowStore.PhaseId(operationId, "hook.capture.completed"), "snapshot.capture.completed", DateTimeOffset.UtcNow,
            cluster.LocalPeerId, ContractJson.ToValue(new { operationId, target, result.Result, snapshotId = result.Snapshot?.Id }), CausationId: operationId), CancellationToken.None).ConfigureAwait(false);
        return result;
    }
}
