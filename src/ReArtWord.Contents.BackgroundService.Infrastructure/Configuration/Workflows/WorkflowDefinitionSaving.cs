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
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

/// <summary>通常定義の保存・自動スナップショット・反映各段階を一つの経路で処理します。</summary>
internal sealed class WorkflowDefinitionSaving(IDurableRepository repository, PeerCluster cluster, WorkflowCompiler compiler,
    WorkflowActivation activation, Func<string, CompiledWorkflow?> getEffective, Func<string, bool> isReserved)
{
    private readonly SnapshotCatalog snapshots = new(repository, cluster);
    private readonly ConfigurationOperationJournal journal = new(repository);
    private readonly SnapshotEditProtection editProtection = new(repository, cluster);

    /// <summary>確認済み保存不能だけを用途別の候補採用へ渡します。</summary>
    internal WorkflowAvailability? Availability
    {
        get; set;
    }

    /// <summary>開始済み保存の各段階を一度進めます。</summary>
    /// <returns>保存結果。</returns>
    internal async Task<WorkflowSaveResult> SaveAsync(WorkflowSaveRequest request, bool allowAutoActivation, Guid? restoreOwner = null, CancellationToken cancellationToken = default)
    {
        if (isReserved(request.Definition.Id))
        {
            return ReservedIdResult(request.OperationId);
        }
        CompilationResult compilation = compiler.Compile(request.Definition);
        ConfigurationPhaseResult notExecuted = new(EOutcome.NotExecuted);
        if (compilation.Workflow is null)
        {
            return new WorkflowSaveResult(request.OperationId, null, compilation.Issues, notExecuted, notExecuted, notExecuted, notExecuted);
        }
        string revision = compilation.Workflow.Revision;
        DurableEntry? current = await repository.ReadAsync("workflows", request.Definition.Id, cancellationToken).ConfigureAwait(false);
        StoredWorkflow? old = current?.Value?.Json.Deserialize<StoredWorkflow>();
        if (old is not null && old.Definition.IsDashboard != request.Definition.IsDashboard)
        {
            return KindChangeResult(request.OperationId);
        }
        if (!string.Equals(old?.Revision, request.ExpectedRevision, StringComparison.Ordinal))
        {
            ConfigurationPhaseResult conflict = new(EOutcome.Failed, "Workflow.RevisionConflict");
            return new WorkflowSaveResult(request.OperationId, revision, [], conflict, notExecuted, notExecuted, notExecuted);
        }
        EActivationPolicy policy = WorkflowActivationPolicy.Resolve(getEffective(request.Definition.Id)?.Definition, request.Definition, request.ActivationPolicy);
        StoredWorkflow workflow = new(request.Definition, revision, DateTimeOffset.UtcNow, policy);
        List<DurableMutation> mutations = [new("workflows", request.Definition.Id, current?.Version, NodeValue.FromObject(workflow))];
        DurableMutation? guard = await editProtection.GuardAsync(request.Definition.Id, old?.Definition, request.Definition, restoreOwner, cancellationToken).ConfigureAwait(false);
        if (guard is null)
        {
            return WorkflowStore.UnknownSave(request.OperationId, EOutcome.NotExecuted, "Workflow.RestoreLocked");
        }
        mutations.Add(guard);
        SnapshotCaptureResult? before = null;
        if (restoreOwner is null)
        {
            before = await new WorkflowBeforeSnapshot(repository, cluster).CaptureAsync(request.OperationId, old, request.Definition,
                getEffective(request.Definition.Id)?.Definition, false, cancellationToken).ConfigureAwait(false);
        }
        if (before is not null && before.Result.Outcome != EOutcome.Succeeded)
        {
            return new(request.OperationId, revision, [], notExecuted, notExecuted, notExecuted, notExecuted,
                BeforeSnapshot: before.Result, BeforeSnapshotId: before.Snapshot?.Id);
        }
        DurableEntry? history = await repository.ReadAsync("workflow.history:" + request.Definition.Id, revision, cancellationToken).ConfigureAwait(false);
        if (history is null)
        {
            mutations.Add(new DurableMutation("workflow.history:" + request.Definition.Id, revision, null, NodeValue.FromObject(workflow)));
        }
        DurableReceipt saved = await cluster.ReplicateAsync(request.OperationId, mutations, cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult persistence = new(saved.Outcome, saved.Reason);
        if (saved.Outcome != EOutcome.Succeeded)
        {
            if (saved.Outcome == EOutcome.Failed && Availability is not null)
            {
                WorkflowSaveResult? candidate = await Availability.FailedAsync(request, saved.Reason, cancellationToken).ConfigureAwait(false);
                if (candidate is not null)
                {
                    return candidate with { BeforeSnapshot = before?.Result, BeforeSnapshotId = before?.Snapshot?.Id };
                }
            }
            return new WorkflowSaveResult(request.OperationId, revision, [], persistence, persistence, notExecuted, notExecuted,
                BeforeSnapshot: before?.Result, BeforeSnapshotId: before?.Snapshot?.Id);
        }
        if (Availability is not null)
        {
            await Availability.RememberSavedAsync(workflow, cancellationToken).ConfigureAwait(false);
        }
        await SaveProgressAsync(new WorkflowSaveResult(request.OperationId, revision, [], persistence, persistence, new ConfigurationPhaseResult(EOutcome.Unknown, "Snapshot.InProgress"), notExecuted,
            BeforeSnapshot: before?.Result, BeforeSnapshotId: before?.Snapshot?.Id), cancellationToken).ConfigureAwait(false);
        NodeValue snapshotValue = NodeValue.FromObject(new ConfigurationSnapshotState(workflow, getEffective(workflow.Definition.Id)?.Definition));
        Guid snapshotOperation = WorkflowStore.PhaseId(request.OperationId, "snapshot");
        SnapshotRecord snapshot = new(snapshotOperation.ToString("N"), request.Definition.Id, cluster.LocalPeerId, ESnapshotKind.Configuration, workflow.SavedAt, snapshotValue, ContractJson.Hash(snapshotValue));
        ConfigurationPhaseResult captured = await snapshots.SaveAsync(snapshot, snapshotOperation, cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult activated = notExecuted;
        if (allowAutoActivation && captured.Outcome == EOutcome.Succeeded && policy == EActivationPolicy.Auto)
        {
            await SaveProgressAsync(new WorkflowSaveResult(request.OperationId, revision, [], persistence, persistence, captured, new ConfigurationPhaseResult(EOutcome.Unknown, "Activation.InProgress"),
                BeforeSnapshot: before?.Result, BeforeSnapshotId: before?.Snapshot?.Id), cancellationToken).ConfigureAwait(false);
            activated = await activation.ActivateAsync(new ActivationRequest(WorkflowStore.PhaseId(request.OperationId, "activate"), request.Definition.Id, revision), cancellationToken).ConfigureAwait(false);
        }
        return new WorkflowSaveResult(request.OperationId, revision, [], persistence, persistence, captured, activated,
            BeforeSnapshot: before?.Result, BeforeSnapshotId: before?.Snapshot?.Id);
    }

    /// <summary>通常保存と離脱保存のどちらでも同じ予約名拒否を返します。</summary>
    /// <returns>副作用を開始しない検証結果です。</returns>
    internal static WorkflowSaveResult ReservedIdResult(Guid operationId)
    {
        ConfigurationPhaseResult denied = new(EOutcome.NotExecuted, "Workflow.ReservedConfigurationId");
        return new(operationId, null, [new ValidationIssue("workflow.reservedId", "独立した共通設定の識別子はWorkflow名に使用できません。")], denied, denied, denied, denied);
    }

    /// <summary>同じIDのWFDashboardと実行WFを相互に読み替えません。</summary>
    /// <returns>定義も反映版も変更しない種類変更の拒否です。</returns>
    internal static WorkflowSaveResult KindChangeResult(Guid operationId)
    {
        ConfigurationPhaseResult denied = new(EOutcome.NotExecuted, "Workflow.KindChangeNotAllowed");
        return new(operationId, null, [new("workflow.kind", "WFDashboard and Workflow cannot be converted into each other.")], denied, denied, denied, denied);
    }

    /// <summary>復元内部保存は除き、利用者の保存操作だけ段階結果を更新します。</summary>
    /// <returns>段階記録の完了。</returns>
    private async Task SaveProgressAsync(WorkflowSaveResult result, CancellationToken cancellationToken = default)
    {
        ConfigurationOperation? operation = await journal.ReadAsync("save", result.OperationId, cancellationToken).ConfigureAwait(false);
        if (operation is not null)
        {
            await journal.CompleteAsync("save", result.OperationId, result, cancellationToken).ConfigureAwait(false);
        }
    }

}
