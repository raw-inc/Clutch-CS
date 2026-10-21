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

/// <summary>当初範囲、実適用範囲と失敗時補償範囲を分けて復元します。</summary>
public sealed class SnapshotService
{
    private readonly PeerCluster cluster;
    private readonly SnapshotCatalog catalog;
    private readonly SnapshotCapture capture;
    private readonly SnapshotEndpoint endpoint;
    private readonly ConfigurationOperationJournal journal;
    private readonly SnapshotRequestSender sender;
    private readonly SnapshotScopeExpander scopes;
    private readonly SnapshotEditProtection protection;
    private readonly SnapshotEditorArbitration editors;

    /// <summary>取得・復元操作の開始と確認結果を通知する共通Hook。</summary>
    public IWorkflowEvents? Events
    {
        get => capture.Events;
        set => capture.Events = value;
    }

    /// <summary>既存の共有保存・管理要求・定義管理を接続します。</summary>
    public SnapshotService(IDurableRepository repository, PeerCluster cluster, WorkflowStore workflows)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(workflows);
        this.cluster = cluster;
        catalog = new SnapshotCatalog(repository, cluster);
        capture = new SnapshotCapture(repository, cluster);
        endpoint = new SnapshotEndpoint(cluster, workflows);
        journal = new ConfigurationOperationJournal(repository);
        sender = new SnapshotRequestSender(repository, cluster);
        scopes = new SnapshotScopeExpander(workflows, cluster);
        protection = new SnapshotEditProtection(repository, cluster);
        editors = new SnapshotEditorArbitration(cluster, endpoint, protection, repository);
    }

    /// <summary>未保存編集の確認と明示取消を接続します。</summary>
    public ISnapshotEditor? Editor
    {
        get => editors.Editor;
        set => editors.Editor = value;
    }

    /// <summary>復元を開始せず現在の編集状態を確認します。</summary>
    /// <returns>元要求の指紋と未確認編集です。</returns>
    public Task<SnapshotRestorePreparation> PrepareRestoreAsync(SnapshotRestoreRequest request, CancellationToken cancellationToken = default) => editors.PrepareAsync(request, cancellationToken);

    /// <summary>元復元要求の範囲だけを調停します。</summary>
    /// <returns>保存済み確認または取消の結果です。</returns>
    public Task<ConfigurationPhaseResult> ResolveDraftAsync(SnapshotDraftResolution request, CancellationToken cancellationToken = default) => editors.ResolveAsync(request, cancellationToken);

    /// <summary>実行時状態の明示対応範囲を登録します。</summary>
    public void RegisterRuntime(IRuntimeSnapshotParticipant participant) => endpoint.Register(participant);

    /// <summary>ワークフロー以外の設定領域をConfiguration種別として登録します。</summary>
    public void RegisterConfiguration(IRuntimeSnapshotParticipant participant) => endpoint.RegisterConfiguration(participant);

    /// <summary>準備済み設定範囲への通常編集を止めるための状態です。</summary>
    /// <returns>復元操作が当該設定を保護しているか。</returns>
    public bool IsConfigurationLocked(string id) => endpoint.IsConfigurationLocked(id);

    /// <summary>現在の所属を明示的なPeerとノード集合へ固定します。</summary>
    /// <returns>展開対象または範囲の問題です。</returns>
    public Task<SnapshotScopeResult> ExpandScopeAsync(SnapshotScopeRequest request, CancellationToken cancellationToken = default) => scopes.ExpandAsync(request, cancellationToken);

    /// <summary>他Peerを含む復元中の編集保護を照会します。</summary>
    /// <returns>選択ノード範囲と元操作です。</returns>
    public Task<IReadOnlyList<SnapshotEditLock>> GetEditLocksAsync(CancellationToken cancellationToken = default) => protection.ReadAsync(cancellationToken);

    /// <summary>取得処理を再実行せず全段階の記録を照会します。</summary>
    /// <returns>記録済み取得結果、開始中Unknown、未受付ならnull。</returns>
    public Task<SnapshotCaptureResult?> GetCaptureOperationAsync(Guid operationId, CancellationToken cancellationToken = default) => capture.GetOperationAsync(operationId, cancellationToken);

    /// <summary>固定条件に触れず復元操作の操作履歴だけを読みます。</summary>
    /// <returns>記録済み対象別結果、開始中Unknown、未受付ならnull。</returns>
    public async Task<SnapshotRestoreResult?> GetRestoreOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        ConfigurationOperation? operation = await journal.ReadAsync("restore", operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return null;
        }
        return operation.Result?.Deserialize<SnapshotRestoreResult>() ?? new SnapshotRestoreResult(operationId, EOutcome.Unknown, [], "Snapshot.RestoreInProgress");
    }

    /// <summary>一対象の実状態を取得して共有保存します。</summary>
    /// <returns>取得・保存の実行結果。</returns>
    public Task<SnapshotCaptureResult> CaptureAsync(Guid operationId, SnapshotTarget target, CancellationToken cancellationToken = default) => capture.CaptureAsync(operationId, target, false, cancellationToken);

    /// <summary>手動保存の永久保持指定を取得操作の固定条件へ含めます。</summary>
    /// <returns>元要求の取得と保存結果です。</returns>
    public Task<SnapshotCaptureResult> CaptureAsync(SnapshotCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return capture.CaptureAsync(request.OperationId, request.Target, request.Permanent, cancellationToken);
    }

    /// <summary>選択内容を事前検証し、失敗時に当初範囲を補償します。</summary>
    /// <returns>全体と対象別の確認結果。</returns>
    public async Task<SnapshotRestoreResult> RestoreAsync(SnapshotRestoreRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ContractJson.Copy(request);
        return await RestoreJournaledAsync(request, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>結果不明の元段階を照会して、確認できた地点から明示回復します。</summary>
    /// <returns>元操作の更新結果。</returns>
    public Task<SnapshotRestoreResult> RecoverAsync(SnapshotRestoreRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RestoreJournaledAsync(ContractJson.Copy(request), true, cancellationToken);
    }

    /// <summary>同一操作を通常再送で実施し直しません。</summary>
    /// <returns>保存した結果または明示回復結果。</returns>
    private async Task<SnapshotRestoreResult> RestoreJournaledAsync(SnapshotRestoreRequest request, bool recover, CancellationToken cancellationToken = default)
    {
        ConfigurationOperation? operation = await journal.ReadAsync("restore", request.OperationId, cancellationToken).ConfigureAwait(false);
        if (recover && operation is null)
        {
            return new SnapshotRestoreResult(request.OperationId, EOutcome.NotExecuted, [], "Snapshot.OriginalOperationMissing");
        }
        if (operation is not null)
        {
            if (!string.Equals(operation.Fingerprint, ContractJson.Hash(request), StringComparison.Ordinal))
            {
                return new SnapshotRestoreResult(request.OperationId, EOutcome.Failed, [], "Snapshot.OperationMismatch");
            }
            SnapshotRestoreResult? known = operation.Result?.Deserialize<SnapshotRestoreResult>();
            if (!recover || known?.Outcome is not (null or EOutcome.Unknown))
            {
                return known ?? new SnapshotRestoreResult(request.OperationId, EOutcome.Unknown, [], "Snapshot.OperationInterrupted");
            }
        }
        else
        {
            JsonElement members = JsonSerializer.SerializeToElement(cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).ToArray());
            string operationKind = "restore";
            if (!await journal.BeginAsync(operationKind, request.OperationId, request, members, cancellationToken).ConfigureAwait(false))
            {
                return new SnapshotRestoreResult(request.OperationId, EOutcome.Unknown, [], "Snapshot.StartUnconfirmed");
            }
            operation = await journal.ReadAsync("restore", request.OperationId, cancellationToken).ConfigureAwait(false);
        }
        PeerInfo[] fixedMembers = operation!.Context?.Deserialize<PeerInfo[]>() ?? [];
        if (recover && !fixedMembers.All(member => cluster.GetPeers().Any(peer => peer.InstanceId == member.InstanceId && PeerStatusRules.CanParticipate(peer.Status))))
        {
            return new SnapshotRestoreResult(request.OperationId, EOutcome.Unknown, [], "Snapshot.OriginalParticipantUnavailable");
        }
        string phase = "hook.restore.started";
        string name = "snapshot.restore.started";
        if (recover)
        {
            phase = "hook.restore.recovering";
            name = "snapshot.restore.recovering";
        }
        await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(WorkflowStore.PhaseId(request.OperationId, phase), name, DateTimeOffset.UtcNow,
            cluster.LocalPeerId, ContractJson.ToValue(new { request.OperationId, request.At, targets = request.Selections.Select(selection => selection.Target) }), CausationId: request.OperationId), CancellationToken.None).ConfigureAwait(false);
        SnapshotRestoreResult result = await RestoreCoreAsync(request, fixedMembers, cancellationToken).ConfigureAwait(false);
        await journal.CompleteAsync("restore", request.OperationId, result, CancellationToken.None).ConfigureAwait(false);
        await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(WorkflowStore.PhaseId(request.OperationId, "hook.restore.completed:" + ContractJson.Hash(result)),
            "snapshot.restore.completed", DateTimeOffset.UtcNow, cluster.LocalPeerId, ContractJson.ToValue(result), CausationId: request.OperationId), CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>元操作の選択と参加集合で復元します。</summary>
    /// <returns>復元結果。</returns>
    private async Task<SnapshotRestoreResult> RestoreCoreAsync(SnapshotRestoreRequest request, PeerInfo[] members, CancellationToken cancellationToken = default)
    {
        if (request.Selections.Select(item => item.Target).Distinct().Count() != request.Selections.Count)
        {
            return new SnapshotRestoreResult(request.OperationId, EOutcome.NotExecuted, [], "Snapshot.DuplicateTarget");
        }
        if (request.Selections.Any(item => !SnapshotNodeSelection.Valid(item.Target)))
        {
            return new SnapshotRestoreResult(request.OperationId, EOutcome.NotExecuted, [], "Snapshot.InvalidNodeSelection");
        }
        if (request.Selections.Where(item => !item.Excluded).GroupBy(item => (item.Target.Id, item.Target.PeerId, item.Target.Kind)).Any(group => group.Count() != 1))
        {
            return new SnapshotRestoreResult(request.OperationId, EOutcome.NotExecuted, [], "Snapshot.MultipleCandidatesForScope");
        }
        Dictionary<SnapshotTarget, SnapshotTargetResult> results = request.Selections.ToDictionary(item => item.Target, item => Empty(item.Target));
        Dictionary<SnapshotTarget, SnapshotRecord?> selected = [];
        IReadOnlyList<SnapshotRecord> available = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (SnapshotSelection selection in request.Selections)
        {
            if (selection.Excluded)
            {
                results[selection.Target] = Empty(selection.Target) with { Warning = "Snapshot.ExplicitlyExcluded" };
                continue;
            }
            SnapshotRecord? snapshot = Select(selection, request.At, available);
            if (snapshot is null && (selection.SnapshotId is not null || request.At is null))
            {
                return new SnapshotRestoreResult(request.OperationId, EOutcome.NotExecuted, results.Values.ToArray(), "Snapshot.ExplicitCandidateMissing");
            }
            if (snapshot is not null && (!string.Equals(snapshot.Hash, ContractJson.Hash(snapshot.Value), StringComparison.Ordinal)
                || !SnapshotNodeSelection.Covers(snapshot, selection.Target)))
            {
                return new SnapshotRestoreResult(request.OperationId, EOutcome.NotExecuted, results.Values.ToArray(), "Snapshot.CandidateInvalid");
            }
            selected.Add(selection.Target, snapshot);
            if (snapshot is null)
            {
                results[selection.Target] = Empty(selection.Target) with { Warning = "Snapshot.NoCandidateAtTime" };
            }
        }
        if (!selected.Values.Any(value => value is not null))
        {
            return new SnapshotRestoreResult(request.OperationId, EOutcome.NotExecuted, results.Values.ToArray(), "Snapshot.NoApplyTargets");
        }
        return await new SnapshotRestoration(cluster, catalog, sender, endpoint, protection, editors).RestoreAsync(request.OperationId, members, selected, results, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>時点指定は対象ごとの直前を選びます。</summary>
    /// <returns>選択候補。</returns>
    private static SnapshotRecord? Select(SnapshotSelection selection, DateTimeOffset? at, IReadOnlyList<SnapshotRecord> available)
    {
        if (selection.SnapshotId is not null)
        {
            return available.FirstOrDefault(snapshot => string.Equals(snapshot.Id, selection.SnapshotId, StringComparison.Ordinal));
        }
        return available.Where(snapshot => SnapshotNodeSelection.Covers(snapshot, selection.Target) && snapshot.CapturedAt <= at).OrderByDescending(snapshot => snapshot.CapturedAt).FirstOrDefault();
    }

    /// <summary>未実施の初期結果です。</summary>
    /// <returns>対象結果。</returns>
    private static SnapshotTargetResult Empty(SnapshotTarget target) => new(target, new ConfigurationPhaseResult(EOutcome.NotExecuted), new ConfigurationPhaseResult(EOutcome.NotExecuted));
}
