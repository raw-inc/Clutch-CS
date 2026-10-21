using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Detached;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

/// <summary>定義保存、共有、スナップショット、有効化を個別結果として管理します。</summary>
public sealed class WorkflowStore
{
    private readonly IDurableRepository repository;
    private readonly PeerCluster cluster;
    private readonly WorkflowCompiler compiler;
    private readonly WorkflowActivation activation;
    private readonly WorkflowDefinitionSaving saving;
    private readonly DetachedWorkflowStore detached;
    private readonly DetachedWorkflowReintegration reintegration;
    private readonly WorkflowAvailability availability;
    private readonly ConfigurationOperationJournal journal;
    private readonly SnapshotEditProtection editProtection;
    private readonly ConcurrentDictionary<string, Guid> writeOwners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> configurationIds = new(StringComparer.Ordinal);

    /// <summary>定義検証と既存の共有保存境界を接続します。</summary>
    public WorkflowStore(IDurableRepository repository, PeerCluster cluster, WorkflowCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(compiler);
        this.repository = repository;
        this.cluster = cluster;
        this.compiler = compiler;
        activation = new WorkflowActivation(repository, cluster, compiler);
        availability = new(repository, cluster, compiler, activation);
        activation.Availability = availability;
        saving = new(repository, cluster, compiler, activation, id => GetEffective?.Invoke(id), id => configurationIds.ContainsKey(id) || string.Equals(id, ClusterConfigurationStore.Id, StringComparison.Ordinal));
        saving.Availability = availability;
        detached = new(repository, cluster, compiler, activation);
        reintegration = new(repository, cluster, detached, this);
        journal = new ConfigurationOperationJournal(repository);
        editProtection = new SnapshotEditProtection(repository, cluster);
        activation.IsWriteLocked = id => writeOwners.ContainsKey(id);
    }

    /// <summary>rootSupervisorが行うローカル実適用です。</summary>
    public Func<CompiledWorkflow, CancellationToken, Task<ConfigurationPhaseResult>>? ActivateHandler
    {
        get => activation.Handler;
        set => activation.Handler = value;
    }

    /// <summary>実際に有効な計画の参照です。</summary>
    public Func<string, CompiledWorkflow?>? GetEffective
    {
        get => activation.GetEffective;
        set => activation.GetEffective = value;
    }

    /// <summary>離脱時の設定編集を種類別のローカル運用方針へ接続します。未指定は許可です。</summary>
    public Func<bool>? CanEditDetached
    {
        get; set;
    }

    /// <summary>用途別の保存不能・未保存採用方針を接続します。</summary>
    public AvailabilityService? Availability
    {
        get => availability.Service;
        set => availability.Service = value;
    }

    /// <summary>開始前にeffectiveがなかった場合の補償です。</summary>
    public Func<string, CancellationToken, Task<ConfigurationPhaseResult>>? DeactivateHandler
    {
        get => activation.DeactivateHandler;
        set => activation.DeactivateHandler = value;
    }

    /// <summary>復元が変更を保護しているワークフローを判定します。</summary>
    internal Func<string, bool>? IsRestoreLocked
    {
        get; set;
    }

    /// <summary>保存済みの現在版を取得します。</summary>
    /// <returns>定義と保存版。</returns>
    public async Task<StoredWorkflow?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (cluster.IsDetached)
        {
            return await detached.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        }
        DurableEntry? entry = await repository.ReadAsync("workflows", workflowId, cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<StoredWorkflow>();
    }

    /// <summary>保存定義を列挙します。</summary>
    /// <returns>保存済み定義。</returns>
    public Task<IReadOnlyList<StoredWorkflow>> ListAsync(CancellationToken cancellationToken = default) => detached.ListAsync(cluster.IsDetached, cancellationToken);

    /// <summary>共有に戻していない離脱時変更と現在の比較結果です。</summary>
    /// <returns>未共有変更の一覧です。</returns>
    public Task<IReadOnlyList<DetachedWorkflowState>> GetDetachedAsync(CancellationToken cancellationToken = default) => reintegration.GetAsync(cancellationToken);

    /// <summary>再参加後の非競合だけを同じ保存経路で共有します。</summary>
    /// <returns>統合確認です。</returns>
    public Task ReconcileDetachedAsync(CancellationToken cancellationToken = default) => reintegration.ReconcileAsync(cancellationToken);

    /// <summary>ローカル版を確認した人の比較選択または明示取消を受け付けます。</summary>
    /// <returns>統合候補、競合または保存段階の結果です。</returns>
    public Task<WorkflowMergeResult> ResolveDetachedAsync(DetachedWorkflowResolution request, CancellationToken cancellationToken = default) => reintegration.ResolveAsync(request, cancellationToken);

    /// <summary>未保存編集も復元中の対象範囲を書き換えません。</summary>
    /// <returns>選択範囲の変更が許可されるか。</returns>
    internal async Task<bool> CanEditAsync(WorkflowDefinition current, WorkflowDefinition proposed, CancellationToken cancellationToken = default)
    {
        if (cluster.IsDetached)
        {
            return CanEditDetached?.Invoke() != false;
        }
        return await editProtection.GuardAsync(current.Id, current, proposed, cancellationToken: cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>保存操作全体の最新確認済み段階を読みます。</summary>
    /// <returns>操作結果、未受理ならnull。</returns>
    public Task<WorkflowSaveResult?> GetSaveOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => journal.ReadSaveAsync(operationId, cancellationToken);

    /// <summary>保存・共有と自動スナップショット成功後だけ既定反映へ進みます。</summary>
    /// <returns>各段階の実行結果。</returns>
    public async Task<WorkflowSaveResult> SaveAsync(WorkflowSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ContractJson.Copy(request);
        if (configurationIds.ContainsKey(request.Definition.Id) || string.Equals(request.Definition.Id, ClusterConfigurationStore.Id, StringComparison.Ordinal))
        {
            return WorkflowDefinitionSaving.ReservedIdResult(request.OperationId);
        }
        if (cluster.IsDetached && (CanEditDetached?.Invoke() == false || configurationIds.ContainsKey(request.Definition.Id)))
        {
            return UnknownSave(request.OperationId, EOutcome.NotExecuted, "Detached.EditNotAllowed");
        }
        if (!TryReserve(request.Definition.Id, request.OperationId))
        {
            return UnknownSave(request.OperationId, EOutcome.NotExecuted, "Workflow.WriteBusy");
        }
        try
        {
            return await SaveTrackedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return await availability.FailedAsync(request, "Persistence.WriteUnavailable", cancellationToken).ConfigureAwait(false) ?? UnknownSave(request.OperationId, EOutcome.Unknown, "Workflow.SaveUnconfirmed");
        }
        finally
        {
            ReleaseReservation(request.Definition.Id, request.OperationId);
        }
    }

    /// <summary>実施済み操作を再送で進め直さない保存処理です。</summary>
    /// <returns>段階結果。</returns>
    private async Task<WorkflowSaveResult> SaveTrackedAsync(WorkflowSaveRequest request, CancellationToken cancellationToken = default)
    {
        if (IsRestoreLocked?.Invoke(request.Definition.Id) == true)
        {
            return UnknownSave(request.OperationId, EOutcome.NotExecuted, "Workflow.RestoreLocked");
        }
        string fingerprint = ContractJson.Hash(request);
        ConfigurationOperation? known = await journal.ReadAsync("save", request.OperationId, cancellationToken).ConfigureAwait(false);
        if (known is not null)
        {
            if (!string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return UnknownSave(request.OperationId, EOutcome.Failed, "Workflow.OperationMismatch");
            }
            return known.Result?.Deserialize<WorkflowSaveResult>() ?? UnknownSave(request.OperationId, EOutcome.Unknown, "Workflow.OperationInterrupted");
        }
        string operationKind = "save";
        if (!await journal.BeginAsync(operationKind, request.OperationId, request, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return await availability.FailedAsync(request, "Workflow.StartUnconfirmed", cancellationToken).ConfigureAwait(false) ?? UnknownSave(request.OperationId, EOutcome.Unknown, "Workflow.StartUnconfirmed");
        }
        WorkflowSaveResult result;
        if (cluster.IsDetached)
        {
            result = await detached.SaveAsync(request, cancellationToken).ConfigureAwait(false) with { LocalOnly = true };
        }
        else
        {
            result = await saving.SaveAsync(request, true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        await journal.CompleteAsync("save", request.OperationId, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>切断中の編集を三者比較し、意味検証後に保存します。</summary>
    /// <returns>競合候補または保存結果。</returns>
    public async Task<WorkflowMergeResult> MergeAsync(WorkflowMergeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DurableEntry? originalEntry = await repository.ReadAsync("workflow.history:" + request.Edited.Id, request.BaseRevision, cancellationToken).ConfigureAwait(false);
        if (cluster.IsDetached)
        {
            originalEntry = await repository.ReadAsync("peer:" + cluster.LocalPeerId + ":detached-history:" + request.Edited.Id, request.BaseRevision, cancellationToken).ConfigureAwait(false) ?? originalEntry;
        }
        StoredWorkflow? original = originalEntry?.Value?.Json.Deserialize<StoredWorkflow>();
        StoredWorkflow? current = await GetAsync(request.Edited.Id, cancellationToken).ConfigureAwait(false);
        if (original is null || current is null)
        {
            return new WorkflowMergeResult(null, [], [new ValidationIssue("merge.baseMissing", "編集元または現在版がありません。")], null);
        }
        CompilationResult edited = compiler.Compile(request.Edited);
        if (edited.Workflow is null)
        {
            return new WorkflowMergeResult(null, [], edited.Issues, null);
        }
        WorkflowMergeResult merged = WorkflowEditorMerge.Merge(original.Definition, current.Definition, request);
        if (merged.Conflicts.Count != 0 || merged.Candidate is null)
        {
            return merged;
        }
        CompilationResult validated = compiler.Compile(merged.Candidate);
        if (validated.Workflow is null)
        {
            return merged with { Validation = validated.Issues };
        }
        WorkflowSaveResult saved = await SaveAsync(new WorkflowSaveRequest(request.OperationId, merged.Candidate, current.Revision, request.ActivationPolicy), cancellationToken).ConfigureAwait(false);
        return merged with { Save = saved };
    }

    /// <summary>保存版を明示反映します。</summary>
    /// <returns>ローカル有効化結果。</returns>
    public async Task<ConfigurationPhaseResult> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryReserve(request.WorkflowId, request.OperationId))
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Workflow.WriteBusy");
        }
        try
        {
            return await journal.ActivateAsync(request, ActivateCoreAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseReservation(request.WorkflowId, request.OperationId);
        }
    }

    /// <summary>一度だけ許可された反映を、現在の保存方式へ渡します。</summary>
    /// <returns>ローカル候補または共有保存版の反映結果です。</returns>
    private async Task<ConfigurationPhaseResult> ActivateCoreAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        if (cluster.IsDetached)
        {
            if (CanEditDetached?.Invoke() == false)
            {
                return new(EOutcome.NotExecuted, "Detached.EditNotAllowed");
            }
            return await detached.ActivateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        ConfigurationPhaseResult? candidate = await availability.ActivateAsync(request, cancellationToken).ConfigureAwait(false);
        return candidate ?? await activation.ActivateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存候補と実有効版の差分を、反映直前に照合する識別子付きで取得します。</summary>
    /// <returns>対象、差分、確認可能・不能の影響です。</returns>
    public Task<ConfigurationImpactPreview> InspectImpactAsync(string id, CancellationToken cancellationToken = default)
        => WorkflowImpact.InspectAsync(this, repository, cluster.LocalPeerId, id, cancellationToken);

    /// <summary>通常保存と復元を同じスコープ境界で直列化します。</summary>
    /// <returns>取得できたか。</returns>
    internal bool TryReserve(string id, Guid owner) => writeOwners.TryAdd(id, owner);

    /// <summary>自分が保護しているスコープだけを解放します。</summary>
    internal void ReleaseReservation(string id, Guid owner)
    {
        if (writeOwners.TryGetValue(id, out Guid current) && current == owner)
        {
            writeOwners.TryRemove(id, out _);
        }
    }

    /// <summary>saved最新ではなく実有効版だけを再起動復元します。</summary>
    /// <returns>復元確認。</returns>
    public async Task RestoreEffectiveAsync(CancellationToken cancellationToken = default)
    {
        await activation.RestoreEffectiveAsync(cancellationToken).ConfigureAwait(false);
        await availability.RestoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>共有反映希望をローカル実行へ適用します。</summary>
    /// <returns>反映確認。</returns>
    public Task ReconcileActivationAsync(CancellationToken cancellationToken = default) => activation.ReconcileAsync(cancellationToken);

    /// <summary>確認版をスナップショットで保護して共有削除します。</summary>
    /// <returns>削除の各段階です。</returns>
    public Task<WorkflowDeleteResult> DeleteAsync(WorkflowDeleteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool allowed = !cluster.IsDetached && !configurationIds.ContainsKey(request.WorkflowId) && !string.Equals(request.WorkflowId, ClusterConfigurationStore.Id, StringComparison.Ordinal) && IsRestoreLocked?.Invoke(request.WorkflowId) != true;
        return new WorkflowDeletion(this, repository, cluster, activation).DeleteAsync(request, allowed, cancellationToken);
    }

    /// <summary>元の削除操作を照会します。</summary>
    /// <returns>記録された結果です。</returns>
    public Task<WorkflowDeleteResult?> GetDeleteOperationAsync(Guid id, CancellationToken cancellationToken = default) => new WorkflowDeletion(this, repository, cluster, activation).ReadAsync(id, cancellationToken);

    /// <summary>Ready前に共有済み有効定義をローカル採用し、初期設定の保存は行いません。</summary>
    /// <returns>空と対象別の利用不能を示す準備結果です。</returns>
    public Task<LocalPreparationResult> PrepareLocalAsync(CancellationToken cancellationToken = default) => activation.PrepareLocalAsync(cancellationToken);

    /// <summary>復元候補にも通常と同じ意味検証を適用します。</summary>
    /// <returns>設定・接続の検証指摘。</returns>
    internal IReadOnlyList<ValidationIssue> Validate(WorkflowDefinition definition) => compiler.Compile(definition).Issues;

    /// <summary>同じスナップショット識別子を別種類の設定で使用させません。</summary>
    internal void ReserveConfigurationId(string id) => configurationIds.TryAdd(id, 0);

    /// <summary>補償用に保存版と実有効版を取得します。</summary>
    /// <returns>開始前状態。</returns>
    internal async Task<ConfigurationSnapshotState?> CaptureConfigurationAsync(string id, CancellationToken cancellationToken = default)
    {
        StoredWorkflow? saved = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            return null;
        }
        return new ConfigurationSnapshotState(saved, GetEffective?.Invoke(id)?.Definition);
    }

    /// <summary>スコープ保護済みの復元処理だけが保存・有効化を進めます。</summary>
    /// <returns>復元の実行結果。</returns>
    internal async Task<ConfigurationPhaseResult> RestoreConfigurationAsync(Guid operationId, ConfigurationSnapshotState state, Guid? restoreOwner = null, CancellationToken cancellationToken = default)
    {
        StoredWorkflow? current = await GetAsync(state.Saved.Definition.Id, cancellationToken).ConfigureAwait(false);
        WorkflowSaveResult saved = await saving.SaveAsync(new WorkflowSaveRequest(PhaseId(operationId, "restore-save"), state.Saved.Definition, current?.Revision, state.Saved.ActivationPolicy), false, restoreOwner, cancellationToken).ConfigureAwait(false);
        if (saved.Saved.Outcome != EOutcome.Succeeded)
        {
            return saved.Saved;
        }
        return await activation.RestoreAsync(operationId, state.Saved.Definition.Id, state.Effective, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>論理操作から各段階の一定IDを作ります。</summary>
    /// <returns>段階別操作ID。</returns>
    internal static Guid PhaseId(Guid operationId, string phase) => new(Convert.FromHexString(ContractJson.Hash(new { operationId, phase })).AsSpan(0, 16));

    /// <summary>確認できない段階を成功へ丸めず返します。</summary>
    /// <returns>同じ非成功状態を明示した結果。</returns>
    internal static WorkflowSaveResult UnknownSave(Guid operationId, EOutcome outcome, string reason)
    {
        ConfigurationPhaseResult phase = new(outcome, reason);
        return new WorkflowSaveResult(operationId, null, [], phase, phase, phase, phase);
    }

}
