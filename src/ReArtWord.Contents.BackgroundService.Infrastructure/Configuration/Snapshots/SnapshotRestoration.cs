using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>固定範囲の保護・適用・補償と解除を実行します。</summary>
internal sealed class SnapshotRestoration(PeerCluster cluster, SnapshotCatalog catalog, SnapshotRequestSender sender, SnapshotEndpoint endpoint, SnapshotEditProtection protection, SnapshotEditorArbitration editors)
{
    private bool releaseUnconfirmed;

    /// <summary>固定参加集合の下で準備・適用・比較・補償を行います。</summary>
    /// <returns>全体結果。</returns>
    public async Task<SnapshotRestoreResult> RestoreAsync(Guid operationId, PeerInfo[] members, Dictionary<SnapshotTarget, SnapshotRecord?> selected, Dictionary<SnapshotTarget, SnapshotTargetResult> results, CancellationToken cancellationToken = default)
    {
        SnapshotRestoreResult result = await RestoreCoreAsync(operationId, members, selected, results, cancellationToken).ConfigureAwait(false);
        if (releaseUnconfirmed)
        {
            return result with { Outcome = EOutcome.Unknown, Reason = "Snapshot.ReleaseUnconfirmed" };
        }
        return result;
    }

    /// <summary>処理結果と保護解除の確認を別々に保持します。</summary>
    /// <returns>適用または補償の結果です。</returns>
    private async Task<SnapshotRestoreResult> RestoreCoreAsync(Guid operationId, PeerInfo[] members, Dictionary<SnapshotTarget, SnapshotRecord?> selected, Dictionary<SnapshotTarget, SnapshotTargetResult> results, CancellationToken cancellationToken = default)
    {
        Dictionary<SnapshotTarget, NodeValue> before = [];
        List<SnapshotTarget> locked = [];
        bool unknown = false;
        try
        {
            SnapshotEditLock[] desired = selected.Keys.Select(target => endpoint.EditLock(operationId, target)).OfType<SnapshotEditLock>().DistinctBy(ContractJson.Hash).ToArray();
            ConfigurationPhaseResult reserved = await protection.AcquireAsync(operationId, desired, cancellationToken).ConfigureAwait(false);
            if (reserved.Outcome != EOutcome.Succeeded)
            {
                unknown = reserved.Outcome == EOutcome.Unknown;
                return new SnapshotRestoreResult(operationId, reserved.Outcome, results.Values.ToArray(), reserved.Reason);
            }
            SnapshotRestorePreparation editing = await editors.PrepareAsync(new SnapshotRestoreRequest(operationId, selected.Keys.Select(target => new SnapshotSelection(target)).ToArray()), cancellationToken).ConfigureAwait(false);
            if (!editing.Ready)
            {
                return new SnapshotRestoreResult(operationId, EOutcome.NotExecuted, results.Values.ToArray(), editing.Reason);
            }
            foreach (KeyValuePair<SnapshotTarget, SnapshotRecord?> item in selected)
            {
                PeerInfo? peer = members.FirstOrDefault(member => string.Equals(member.PeerId, item.Key.PeerId, StringComparison.Ordinal));
                if (peer is null || !MembersRemain(members))
                {
                    return new SnapshotRestoreResult(operationId, EOutcome.NotExecuted, results.Values.ToArray(), "Snapshot.ParticipantUnavailable");
                }
                locked.Add(item.Key);
                Dictionary<string, NodeValue> configurations = selected.Where(pair => pair.Value is not null && pair.Key.Kind == ESnapshotKind.Configuration && string.Equals(pair.Key.PeerId, peer.PeerId, StringComparison.Ordinal)).ToDictionary(pair => pair.Key.Id, pair => pair.Value!.Value);
                Dictionary<string, NodeValue> runtimeStates = selected.Where(pair => pair.Value is not null && pair.Key.Kind == ESnapshotKind.Runtime && string.Equals(pair.Key.PeerId, peer.PeerId, StringComparison.Ordinal)).ToDictionary(pair => pair.Key.Id, pair => pair.Value!.Value);
                Dictionary<string, SnapshotTarget> configurationTargets = selected.Keys.Where(target => target.Kind == ESnapshotKind.Configuration && string.Equals(target.PeerId, peer.PeerId, StringComparison.Ordinal)).ToDictionary(target => target.Id);
                ClusterResponse prepared = await sender.SendStepAsync("prepare", "prepare", peer, new SnapshotStep(operationId, item.Key, item.Value?.Value, configurations, runtimeStates, configurationTargets), cancellationToken).ConfigureAwait(false);
                if (prepared.Outcome != EOutcome.Succeeded || prepared.Value is null)
                {
                    unknown = prepared.Outcome == EOutcome.Unknown;
                    return new SnapshotRestoreResult(operationId, prepared.Outcome, results.Values.ToArray(), prepared.Reason);
                }
                before.Add(item.Key, prepared.Value);
                Guid backupId = StepId(operationId, item.Key, "backup");
                SnapshotRecord? savedBackup = await catalog.ReadAsync(backupId.ToString("N"), cancellationToken).ConfigureAwait(false);
                ConfigurationPhaseResult backup = new(EOutcome.Succeeded);
                if (savedBackup is null)
                {
                    backup = await catalog.SaveAsync(Record(backupId, item.Key, prepared.Value), backupId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    DurableReceipt? confirmed = await cluster.ConfirmReplicationAsync(backupId, members, cancellationToken).ConfigureAwait(false);
                    backup = new ConfigurationPhaseResult(confirmed?.Outcome ?? EOutcome.Unknown, confirmed?.Reason);
                }
                if (backup.Outcome != EOutcome.Succeeded)
                {
                    unknown = backup.Outcome == EOutcome.Unknown;
                    return new SnapshotRestoreResult(operationId, backup.Outcome, results.Values.ToArray(), "Snapshot.BackupUnconfirmed");
                }
            }
            bool failed = false;
            foreach (KeyValuePair<SnapshotTarget, SnapshotRecord?> item in selected.Where(item => item.Value is not null).OrderBy(item => item.Key.Kind == ESnapshotKind.Runtime))
            {
                PeerInfo peer = members.Single(member => string.Equals(member.PeerId, item.Key.PeerId, StringComparison.Ordinal));
                if (!MembersRemain(members))
                {
                    failed = true;
                    break;
                }
                ClusterResponse applied = await SendAsync("apply", operationId, item.Key, peer, item.Value!.Value, cancellationToken).ConfigureAwait(false);
                if (applied.Outcome == EOutcome.Succeeded)
                {
                    applied = await SendAsync("compare", operationId, item.Key, peer, item.Value.Value, cancellationToken).ConfigureAwait(false);
                }
                results[item.Key] = results[item.Key] with { Apply = new ConfigurationPhaseResult(applied.Outcome, applied.Reason) };
                if (applied.Outcome != EOutcome.Succeeded)
                {
                    unknown = applied.Outcome == EOutcome.Unknown;
                    failed = true;
                    break;
                }
            }
            if (unknown)
            {
                return new SnapshotRestoreResult(operationId, EOutcome.Unknown, results.Values.ToArray(), "Snapshot.ApplyUnconfirmed");
            }
            if (failed || !MembersRemain(members))
            {
                await CompensateAsync(operationId, members, before, results, cancellationToken).ConfigureAwait(false);
                if (results.Values.Any(item => item.Compensation.Outcome == EOutcome.Unknown))
                {
                    unknown = true;
                    return new SnapshotRestoreResult(operationId, EOutcome.Unknown, results.Values.ToArray(), "Snapshot.CompensationUnconfirmed");
                }
                return new SnapshotRestoreResult(operationId, EOutcome.Failed, results.Values.ToArray(), "Snapshot.RestoreFailed");
            }
            return new SnapshotRestoreResult(operationId, EOutcome.Succeeded, results.Values.ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            unknown = true;
            return new SnapshotRestoreResult(operationId, EOutcome.Unknown, results.Values.ToArray(), "Snapshot.OperationInterrupted");
        }
        finally
        {
            if (!unknown)
            {
                foreach (SnapshotTarget target in locked)
                {
                    PeerInfo? peer = members.FirstOrDefault(member => string.Equals(member.PeerId, target.PeerId, StringComparison.Ordinal));
                    if (peer is not null)
                    {
                        ClusterResponse released = await SendAsync("release", operationId, target, peer, null, CancellationToken.None).ConfigureAwait(false);
                        releaseUnconfirmed |= released.Outcome != EOutcome.Succeeded;
                    }
                }
                if (!releaseUnconfirmed)
                {
                    ConfigurationPhaseResult released = await protection.ReleaseAsync(operationId, CancellationToken.None).ConfigureAwait(false);
                    releaseUnconfirmed = released.Outcome != EOutcome.Succeeded;
                }
            }
        }
    }

    /// <summary>自然欠損により未適用だった対象も開始前状態へ補償します。</summary>
    /// <returns>補償確認。</returns>
    private async Task CompensateAsync(Guid operationId, IReadOnlyList<PeerInfo> members, Dictionary<SnapshotTarget, NodeValue> before, Dictionary<SnapshotTarget, SnapshotTargetResult> results, CancellationToken cancellationToken = default)
    {
        foreach (KeyValuePair<SnapshotTarget, NodeValue> item in before.OrderBy(item => item.Key.Kind == ESnapshotKind.Runtime))
        {
            PeerInfo peer = members.Single(member => string.Equals(member.PeerId, item.Key.PeerId, StringComparison.Ordinal));
            ClusterResponse response = await SendIdentifiedAsync("apply", "compensate", operationId, item.Key, peer, item.Value, cancellationToken).ConfigureAwait(false);
            if (response.Outcome == EOutcome.Succeeded)
            {
                response = await SendIdentifiedAsync("compare", "compensate-compare", operationId, item.Key, peer, item.Value, cancellationToken).ConfigureAwait(false);
            }
            results[item.Key] = results[item.Key] with { Compensation = new ConfigurationPhaseResult(response.Outcome, response.Reason) };
        }
    }

    /// <summary>元操作IDを保ち、各段階だけ別の実行IDにします。</summary>
    /// <returns>対象からの確認。</returns>
    private Task<ClusterResponse> SendAsync(string phase, Guid operationId, SnapshotTarget target, PeerInfo peer, NodeValue? value, CancellationToken cancellationToken = default) => SendIdentifiedAsync(phase, phase, operationId, target, peer, value, cancellationToken);

    /// <summary>補償を適用とは別の一回として識別します。</summary>
    /// <returns>対象からの確認。</returns>
    private Task<ClusterResponse> SendIdentifiedAsync(string phase, string identityPhase, Guid operationId, SnapshotTarget target, PeerInfo peer, NodeValue? value, CancellationToken cancellationToken = default)
    {
        string compare = "compare";
        if (string.Equals(phase, compare, StringComparison.Ordinal))
        {
            identityPhase += Guid.NewGuid().ToString("N");
        }
        return sender.SendAsync(phase, identityPhase, operationId, target, peer, value, cancellationToken);
    }

    /// <summary>当初確認対象を減らして継続しません。</summary>
    /// <returns>全元の実体が通常参加しているか。</returns>
    private bool MembersRemain(IReadOnlyList<PeerInfo> members) => members.All(member => cluster.GetPeers().Any(peer => peer.InstanceId == member.InstanceId && PeerStatusRules.CanParticipate(peer.Status)));

    /// <summary>対象Peerを確認します。</summary>
    /// <returns>通常参加しているPeer。</returns>
    private PeerInfo? FindPeer(string id) => cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, id, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status));

    /// <summary>実取得値のスナップショットを作ります。</summary>
    /// <returns>取得記録。</returns>
    private static SnapshotRecord Record(Guid id, SnapshotTarget target, NodeValue value) => new(id.ToString("N"), target.Id, target.PeerId, target.Kind, DateTimeOffset.UtcNow, value.Copy(), ContractJson.Hash(value), target.NodeIds);

    /// <summary>対象と段階を含めて一意化します。</summary>
    /// <returns>同じ操作で一定のID。</returns>
    private static Guid StepId(Guid id, SnapshotTarget target, string phase) => WorkflowStore.PhaseId(id, ContractJson.Hash(new { target, phase }));

}
