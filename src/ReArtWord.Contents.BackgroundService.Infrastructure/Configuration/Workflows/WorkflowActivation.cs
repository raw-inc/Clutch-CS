using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

/// <summary>反映希望の共有と、このPeerの実有効化を分けます。</summary>
internal sealed class WorkflowActivation
{
    private readonly IDurableRepository repository;
    private readonly PeerCluster cluster;
    private readonly WorkflowCompiler compiler;
    private readonly ConcurrentDictionary<string, ObservedActivation> observed = new(StringComparer.Ordinal);

    /// <summary>未保存採用と通常永続採用の状態を分けます。</summary>
    internal WorkflowAvailability? Availability
    {
        get; set;
    }

    /// <summary>有効化に必要な境界を指定します。</summary>
    public WorkflowActivation(IDurableRepository repository, PeerCluster cluster, WorkflowCompiler compiler)
    {
        this.repository = repository;
        this.cluster = cluster;
        this.compiler = compiler;
    }

    /// <summary>rootSupervisorの実適用です。</summary>
    public Func<CompiledWorkflow, CancellationToken, Task<ConfigurationPhaseResult>>? Handler
    {
        get; set;
    }

    /// <summary>実行中の現在版を取得します。</summary>
    public Func<string, CompiledWorkflow?>? GetEffective
    {
        get; set;
    }

    /// <summary>effective未設定への明示補償を行います。</summary>
    public Func<string, CancellationToken, Task<ConfigurationPhaseResult>>? DeactivateHandler
    {
        get; set;
    }

    /// <summary>保存や復元が保護中の対象を判定します。</summary>
    public Func<string, bool>? IsWriteLocked
    {
        get; set;
    }

    /// <summary>補償時は開始前の実有効版へ反映希望とローカル状態を戻します。</summary>
    /// <returns>実補償結果。</returns>
    public async Task<ConfigurationPhaseResult> RestoreAsync(Guid operationId, string workflowId, WorkflowDefinition? definition, CancellationToken cancellationToken = default)
    {
        DurableEntry? marker = await repository.ReadAsync("workflow.activation", workflowId, cancellationToken).ConfigureAwait(false);
        StoredWorkflow? workflow = null;
        if (definition is not null)
        {
            workflow = new StoredWorkflow(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Explicit);
        }
        NodeValue? value = null;
        if (workflow is not null)
        {
            value = NodeValue.FromObject(workflow);
        }
        DurableReceipt published = await cluster.ReplicateAsync(operationId, [new DurableMutation("workflow.activation", workflowId, marker?.Version, value)], cancellationToken).ConfigureAwait(false);
        if (published.Outcome != EOutcome.Succeeded)
        {
            return new ConfigurationPhaseResult(published.Outcome, published.Reason);
        }
        if (workflow is not null)
        {
            return await ApplyLocalAsync(workflow, cancellationToken).ConfigureAwait(false);
        }
        return await DeactivateLocalAsync(operationId, workflowId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>実適用後にeffective未設定を永続保存します。</summary>
    /// <returns>解除結果。</returns>
    internal async Task<ConfigurationPhaseResult> DeactivateLocalAsync(Guid operationId, string workflowId, CancellationToken cancellationToken = default)
    {
        if (DeactivateHandler is null)
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Activation.DeactivationUnavailable");
        }
        ConfigurationPhaseResult deactivated = await DeactivateHandler(workflowId, cancellationToken).ConfigureAwait(false);
        if (deactivated.Outcome == EOutcome.Succeeded)
        {
            string scope = "peer:" + cluster.LocalPeerId + ":effective";
            DurableEntry? old = await repository.ReadAsync(scope, workflowId, cancellationToken).ConfigureAwait(false);
            DurableReceipt result = await repository.ApplyBatchAsync(WorkflowStore.PhaseId(operationId, "deactivate"), [new DurableMutation(scope, workflowId, old?.Version, null)], cancellationToken).ConfigureAwait(false);
            if (result.Outcome == EOutcome.Succeeded && Availability is not null)
            {
                await Availability.RememberDeactivatedAsync(workflowId, cancellationToken).ConfigureAwait(false);
            }
            return new ConfigurationPhaseResult(result.Outcome, result.Reason);
        }
        return deactivated;
    }

    /// <summary>保存済み版の反映希望を共有し、ローカル適用を確認します。</summary>
    /// <returns>このPeerでの有効化結果。</returns>
    public async Task<ConfigurationPhaseResult> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        DurableEntry? history = await repository.ReadAsync("workflow.history:" + request.WorkflowId, request.Revision, cancellationToken).ConfigureAwait(false);
        StoredWorkflow? workflow = history?.Value?.Json.Deserialize<StoredWorkflow>();
        StoredWorkflow? latest = (await repository.ReadAsync("workflows", request.WorkflowId, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<StoredWorkflow>();
        if (request.ImpactToken is not null && !string.Equals(latest?.Revision, request.Revision, StringComparison.Ordinal))
        {
            return new(EOutcome.NotExecuted, "Activation.ImpactChanged");
        }
        if (latest is not null && string.Equals(latest.Revision, request.Revision, StringComparison.Ordinal))
        {
            workflow = latest;
        }
        if (workflow is null || (workflow.ActivationPolicy == EActivationPolicy.ConfirmImpact && !request.ImpactConfirmed))
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Activation.ConfirmationOrRevisionMissing");
        }
        DurableEntry? old = await repository.ReadAsync("workflow.activation", request.WorkflowId, cancellationToken).ConfigureAwait(false);
        StoredWorkflow? active = old?.Value?.Json.Deserialize<StoredWorkflow>();
        DurableMutation? guard = await new SnapshotEditProtection(repository, cluster).GuardAsync(request.WorkflowId, active?.Definition, workflow.Definition, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (guard is null)
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Workflow.RestoreLocked");
        }

        // guard取得後に候補を再照会します。以後の保存は同じguardのCASと競合させます。
        latest = (await repository.ReadAsync("workflows", request.WorkflowId, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<StoredWorkflow>();
        if (latest is null)
        {
            return new(EOutcome.NotExecuted, "Workflow.DeletedOrMissing");
        }
        if (string.Equals(latest.Revision, request.Revision, StringComparison.Ordinal))
        {
            workflow = latest;
        }
        if (workflow.ActivationPolicy == EActivationPolicy.ConfirmImpact && !request.ImpactConfirmed)
        {
            return new(EOutcome.NotExecuted, "Activation.ConfirmationOrRevisionMissing");
        }
        if ((workflow.ActivationPolicy == EActivationPolicy.ConfirmImpact || request.ImpactToken is not null)
            && (!string.Equals(latest.Revision, request.Revision, StringComparison.Ordinal)
            || !string.Equals(request.ImpactToken, WorkflowImpact.Token(workflow, GetEffective?.Invoke(request.WorkflowId)?.Definition, old?.Version, cluster.LocalPeerId), StringComparison.Ordinal)))
        {
            return new(EOutcome.NotExecuted, "Activation.ImpactChanged");
        }
        DurableReceipt marker = await cluster.ReplicateAsync(request.OperationId, [new DurableMutation("workflow.activation", request.WorkflowId, old?.Version, NodeValue.FromObject(workflow)), guard], cancellationToken).ConfigureAwait(false);
        if (marker.Outcome != EOutcome.Succeeded)
        {
            return new ConfigurationPhaseResult(marker.Outcome, marker.Reason);
        }
        return await ApplyLocalAsync(workflow, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>再起動時は最後の実有効版だけを復元します。</summary>
    /// <returns>復元完了。</returns>
    public async Task RestoreEffectiveAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync("peer:" + cluster.LocalPeerId + ":effective", cancellationToken).ConfigureAwait(false);
        foreach (DurableEntry entry in entries)
        {
            DurableEntry? desired = await repository.ReadAsync("workflow.activation", entry.Key, cancellationToken).ConfigureAwait(false);
            if (desired is { Value: null })
            {
                await DeactivateLocalAsync(Guid.NewGuid(), entry.Key, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (entry.Value is null && GetEffective?.Invoke(entry.Key) is not null && IsWriteLocked?.Invoke(entry.Key) != true)
            {
                await DeactivateLocalAsync(Guid.NewGuid(), entry.Key, cancellationToken).ConfigureAwait(false);
                continue;
            }
            StoredWorkflow? workflow = entry.Value?.Json.Deserialize<StoredWorkflow>();
            if (workflow is not null)
            {
                await ApplyLocalAsync(workflow, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Ready後に到達済み反映希望を実適用します。</summary>
    /// <returns>確認処理の完了。</returns>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
        {
            return;
        }
        await PrepareLocalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ready前でも共有済み定義をローカル採用し、個別不正を独立定義へ波及させません。</summary>
    /// <returns>空と個別の利用不能を区別する準備結果です。</returns>
    public async Task<LocalPreparationResult> PrepareLocalAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> known = observed.Where(pair => ReferenceEquals(GetEffective?.Invoke(pair.Key), pair.Value.Plan))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Fingerprint, StringComparer.Ordinal);
        IReadOnlyList<DurableObservation> entries = await repository.ObserveAsync("workflow.activation", known, cancellationToken: cancellationToken).ConfigureAwait(false);
        string workflowScope = "workflows";
        if (entries.Count == 0 && (await repository.ListAsync(workflowScope, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            observed.Clear();
            return new(ELocalPreparationState.Empty, []);
        }
        List<ValidationIssue> issues = [];
        foreach (string removed in observed.Keys.Except(entries.Select(item => item.Key), StringComparer.Ordinal))
        {
            observed.TryRemove(removed, out _);
        }
        foreach (DurableObservation observation in entries)
        {
            if (IsWriteLocked?.Invoke(observation.Key) == true || observation.Entry is not { } entry)
            {
                continue;
            }
            if (entry.Value is null)
            {
                observed.TryRemove(entry.Key, out _);
                if (GetEffective?.Invoke(entry.Key) is not null)
                {
                    ConfigurationPhaseResult removed = await DeactivateLocalAsync(Guid.NewGuid(), entry.Key, cancellationToken).ConfigureAwait(false);
                    if (removed.Outcome != EOutcome.Succeeded)
                    {
                        issues.Add(new ValidationIssue("workflow.localPreparation", removed.Reason ?? "局所解除を確認できません。", Path: entry.Key));
                    }
                }
                continue;
            }
            ConfigurationPhaseResult applied = await PrepareEntryAsync(entry, observation.Fingerprint, cancellationToken).ConfigureAwait(false);
            if (applied.Outcome != EOutcome.Succeeded)
            {
                issues.Add(new ValidationIssue("workflow.localPreparation", applied.Reason ?? "局所採用を確認できません。", Path: entry.Key));
                if (applied.Outcome == EOutcome.NotExecuted && GetEffective?.Invoke(entry.Key) is not null)
                {
                    ConfigurationPhaseResult isolated = await DeactivateLocalAsync(Guid.NewGuid(), entry.Key, cancellationToken).ConfigureAwait(false);
                    if (isolated.Outcome != EOutcome.Succeeded)
                    {
                        issues.Add(new ValidationIssue("workflow.isolationUnconfirmed", isolated.Reason ?? "対象定義の新規入力停止を確認できません。", Path: entry.Key));
                    }
                }
            }
        }
        return new(ELocalPreparationState.Prepared, issues);
    }

    /// <summary>共有行の対象と版を検証し、機器の起動を行わず定義を採用します。</summary>
    /// <returns>この定義だけのローカル結果です。</returns>
    private async Task<ConfigurationPhaseResult> PrepareEntryAsync(DurableEntry entry, string fingerprint, CancellationToken cancellationToken = default)
    {
        try
        {
            StoredWorkflow? workflow = entry.Value?.Json.Deserialize<StoredWorkflow>();
            if (workflow?.Definition is null || !string.Equals(workflow.Definition.Id, entry.Key, StringComparison.Ordinal))
            {
                return new(EOutcome.NotExecuted, "Activation.TargetMismatch");
            }
            if (Availability?.Preserve(workflow) == true)
            {
                return new(EOutcome.Succeeded, "Availability.UnsavedEffective");
            }
            ConfigurationPhaseResult result;
            if (string.Equals(GetEffective?.Invoke(entry.Key)?.Revision, workflow.Revision, StringComparison.Ordinal)
                && string.Equals(ContractJson.Hash(workflow.Definition), workflow.Revision, StringComparison.Ordinal))
            {
                result = new(EOutcome.Succeeded);
            }
            else
            {
                result = await ApplyLocalAsync(workflow, cancellationToken).ConfigureAwait(false);
            }
            if (result.Outcome == EOutcome.Succeeded && GetEffective?.Invoke(entry.Key) is { } effective
                && string.Equals(effective.Revision, workflow.Revision, StringComparison.Ordinal))
            {
                observed[entry.Key] = new(fingerprint, effective);
            }
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new(EOutcome.NotExecuted, "Activation.InvalidDefinition");
        }
    }

    /// <summary>明示Bの候補を未来の入力へ適用し、保存済みeffective マーカーを変更しません。</summary>
    /// <returns>計画採用自体の結果です。永続保存を成功と偽りません。</returns>
    internal async Task<ConfigurationPhaseResult> ApplyUnsavedAsync(CompiledWorkflow workflow, CancellationToken cancellationToken = default)
    {
        CompilationResult validated = compiler.Compile(workflow.Definition);
        if (validated.Workflow is null || Handler is null || !string.Equals(validated.Workflow.Revision, workflow.Revision, StringComparison.Ordinal))
        {
            return new(EOutcome.NotExecuted, "Activation.UnsavedCandidateInvalid");
        }
        return await Handler(validated.Workflow, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>意味検証と実適用後だけローカルeffectiveを保存します。</summary>
    /// <returns>実適用結果。</returns>
    internal async Task<ConfigurationPhaseResult> ApplyLocalAsync(StoredWorkflow workflow, CancellationToken cancellationToken = default)
    {
        CompilationResult compiled = compiler.Compile(workflow.Definition);
        if (compiled.Workflow is null || Handler is null || !string.Equals(compiled.Workflow.Revision, workflow.Revision, StringComparison.Ordinal))
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Activation.InvalidOrUnavailable");
        }
        ConfigurationPhaseResult applied = await Handler(compiled.Workflow, cancellationToken).ConfigureAwait(false);
        if (applied.Outcome != EOutcome.Succeeded)
        {
            return applied;
        }
        string scope = "peer:" + cluster.LocalPeerId + ":effective";
        DurableEntry? current = await repository.ReadAsync(scope, workflow.Definition.Id, cancellationToken).ConfigureAwait(false);
        DurableReceipt saved = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(scope, workflow.Definition.Id, current?.Version, NodeValue.FromObject(ContractJson.Copy(workflow)))], cancellationToken).ConfigureAwait(false);
        if (saved.Outcome == EOutcome.Succeeded && Availability is not null && !workflow.LocalOnly)
        {
            await Availability.RememberAdoptedAsync(workflow, cancellationToken).ConfigureAwait(false);
        }
        return new ConfigurationPhaseResult(saved.Outcome, saved.Reason);
    }

    /// <summary>本文を検証した反映希望と、その時点で実採用していた計画です。</summary>
    private sealed record ObservedActivation(string Fingerprint, CompiledWorkflow Plan);
}
