using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;

/// <summary>明示保存不能方針を通常定義検証・保存反映の段階へ接続します。</summary>
internal sealed class WorkflowAvailability(IDurableRepository repository, PeerCluster cluster, WorkflowCompiler compiler, WorkflowActivation activation)
{
    private readonly ConcurrentDictionary<string, string?> protectedMarkers = new(StringComparer.Ordinal);

    /// <summary>未指定では通常の旧保存状態維持だけを行います。</summary>
    internal AvailabilityService? Service
    {
        get; set;
    }

    /// <summary>未保存採用中の版を、変更されていない旧マーカーで自動上書きしません。</summary>
    /// <returns>ローカルの明示候補を維持するか。</returns>
    internal bool Preserve(StoredWorkflow desired) => protectedMarkers.TryGetValue(desired.Definition.Id, out string? marker) && string.Equals(marker, desired.Revision, StringComparison.Ordinal) && activation.GetEffective?.Invoke(desired.Definition.Id) is not null;

    /// <summary>保存成功だけを確認し、スナップショット失敗時の旧実有効版を維持します。</summary>
    /// <returns>状態表示の確認です。</returns>
    internal async Task RememberSavedAsync(StoredWorkflow saved, CancellationToken cancellationToken = default)
    {
        if (Service is not null)
        {
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, Dataset(saved.Definition.Id), NodeValue.FromObject(saved), saved.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>削除確認後は代替媒体の旧候補を再採用しません。</summary>
    /// <returns>保存版を残した有効状態と候補の解除です。</returns>
    internal async Task RememberDeactivatedAsync(string id, CancellationToken cancellationToken = default)
    {
        protectedMarkers.TryRemove(id, out _);
        if (Service is not null)
        {
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, Dataset(id), null, null, true, cancellationToken).ConfigureAwait(false);
            StoredWorkflow? saved = await ReadSavedAsync(id, cancellationToken).ConfigureAwait(false);
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, Dataset(id), Value(saved), saved?.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>実適用と永続effective確認後だけ候補採用の状態を解除します。</summary>
    /// <returns>保存版と有効版を分けた記録です。</returns>
    internal async Task RememberAdoptedAsync(StoredWorkflow adopted, CancellationToken cancellationToken = default)
    {
        protectedMarkers.TryRemove(adopted.Definition.Id, out _);
        if (Service is not null)
        {
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, Dataset(adopted.Definition.Id), NodeValue.FromObject(adopted), adopted.Revision, true, cancellationToken).ConfigureAwait(false);
            StoredWorkflow? saved = await ReadSavedAsync(adopted.Definition.Id, cancellationToken).ConfigureAwait(false);
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, Dataset(adopted.Definition.Id), Value(saved), saved?.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>保存本体が未実施と確認できた場合だけ、用途別の明示候補を扱います。</summary>
    /// <returns>候補利用の段階結果、適用対象外ならnullです。</returns>
    internal async Task<WorkflowSaveResult?> FailedAsync(WorkflowSaveRequest request, string? reason, CancellationToken cancellationToken = default)
    {
        bool rejected = reason is "Persistence.VersionConflict" or "Persistence.OperationMismatch" or "Persistence.ConflictSetChanged";
        if (Service is null || cluster.IsDetached || rejected)
        {
            return null;
        }
        StoredWorkflow? saved = await ReadSavedAsync(request.Definition.Id, cancellationToken).ConfigureAwait(false);
        if (saved is not null && saved.Definition.IsDashboard != request.Definition.IsDashboard)
        {
            return WorkflowDefinitionSaving.KindChangeResult(request.OperationId);
        }
        if (!string.Equals(saved?.Revision, request.ExpectedRevision, StringComparison.Ordinal))
        {
            return null;
        }
        string scope = "workflows";
        CompilationResult compiled = compiler.Compile(request.Definition);
        if (compiled.Workflow is null || (await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false)).Any(entry => string.Equals(entry.Key, request.Definition.Id, StringComparison.Ordinal)))
        {
            return null;
        }
        SnapshotEditProtection protection = new(repository, cluster);
        if (await protection.GuardAsync(request.Definition.Id, saved?.Definition, request.Definition, cancellationToken: cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }
        DurableReceipt? known = await cluster.GetOperationAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        if (known?.Outcome is EOutcome.Unknown or EOutcome.Succeeded)
        {
            return null;
        }
        if (!await new WorkflowBeforeSnapshot(repository, cluster).IsProtectedAsync(request.OperationId, saved, request.Definition, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, Dataset(request.Definition.Id), Value(saved), saved?.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        EActivationPolicy policy = WorkflowActivationPolicy.Resolve(activation.GetEffective?.Invoke(request.Definition.Id)?.Definition, request.Definition, request.ActivationPolicy);
        StoredWorkflow candidate = new(request.Definition, compiled.Workflow.Revision, DateTimeOffset.UtcNow, policy);
        Func<NodeValue, CancellationToken, Task<EOutcome>>? apply = null;
        if (policy == EActivationPolicy.Auto)
        {
            apply = ApplyAsync;
        }
        AvailabilityWriteResult result = await Service.HandleSaveFailureAsync(EStoragePurpose.Configuration, Dataset(request.Definition.Id), NodeValue.FromObject(candidate), candidate.Revision, saved?.Revision, request.OperationId, true, apply, cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult unshared = new(EOutcome.NotExecuted, "Availability.LocalCandidate");
        ConfigurationPhaseResult adopted = unshared;
        if (result.AdoptionOutcome is EOutcome adoption)
        {
            adopted = new(adoption, result.Reason);
        }
        return new(request.OperationId, candidate.Revision, [], new(result.PersistenceOutcome, result.Reason), unshared, unshared, adopted, true);
    }

    /// <summary>未保存候補の明示適用でも版と影響確認を維持します。</summary>
    /// <returns>候補があれば実採用結果です。</returns>
    internal async Task<ConfigurationPhaseResult?> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        AvailabilityValue? candidate = Service?.TryGetCandidate(EStoragePurpose.Configuration, Dataset(request.WorkflowId));
        StoredWorkflow? workflow = candidate?.Value?.Json.Deserialize<StoredWorkflow>();
        if (workflow is null || !string.Equals(workflow.Revision, request.Revision, StringComparison.Ordinal))
        {
            return null;
        }
        DurableEntry? saved = await repository.ReadAsync("workflows", request.WorkflowId, cancellationToken).ConfigureAwait(false);
        if (saved is { Value: null })
        {
            return new(EOutcome.NotExecuted, "Workflow.DeletedOrMissing");
        }
        if (workflow.ActivationPolicy == EActivationPolicy.ConfirmImpact && !request.ImpactConfirmed)
        {
            return new(EOutcome.NotExecuted, "Activation.ConfirmationOrRevisionMissing");
        }
        DurableEntry? marker = await repository.ReadAsync("workflow.activation", request.WorkflowId, cancellationToken).ConfigureAwait(false);
        if ((workflow.ActivationPolicy == EActivationPolicy.ConfirmImpact || request.ImpactToken is not null)
            && !string.Equals(request.ImpactToken, WorkflowImpact.Token(workflow, activation.GetEffective?.Invoke(request.WorkflowId)?.Definition, marker?.Version, cluster.LocalPeerId, true), StringComparison.Ordinal))
        {
            return new(EOutcome.NotExecuted, "Activation.ImpactChanged");
        }
        AvailabilityWriteResult result = await Service!.ActivateCandidateAsync(EStoragePurpose.Configuration, Dataset(request.WorkflowId), ApplyAsync, cancellationToken).ConfigureAwait(false);
        EOutcome outcome = EOutcome.NotExecuted;
        if (result.Applied)
        {
            outcome = EOutcome.Succeeded;
        }
        return new(result.AdoptionOutcome ?? outcome, result.Reason);
    }

    /// <summary>再起動で通常有効版を確認した後、明示代替媒体の採用済み候補だけを復旧します。</summary>
    /// <returns>各ワークフローの候補再検証の完了です。</returns>
    internal async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (Service is null || cluster.IsDetached)
        {
            return;
        }
        IReadOnlyList<DurableEntry> saved = await repository.ListAsync("workflows", cancellationToken).ConfigureAwait(false);
        string prefix = "workflow:";
        IReadOnlyList<string> fallback = await Service.ListRecoveryDatasetsAsync(EStoragePurpose.Configuration, cancellationToken).ConfigureAwait(false);
        IEnumerable<string> ids = saved.Select(entry => entry.Key).Concat(fallback.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).Select(id => id[prefix.Length..])).Distinct(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            StoredWorkflow? workflow = await ReadSavedAsync(id, cancellationToken).ConfigureAwait(false);
            await Service.RememberConfirmedAsync(EStoragePurpose.Configuration, Dataset(id), Value(workflow), workflow?.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (await Service.RestoreCandidateAsync(EStoragePurpose.Configuration, Dataset(id), cancellationToken).ConfigureAwait(false) is not null)
            {
                _ = await Service.ActivateCandidateAsync(EStoragePurpose.Configuration, Dataset(id), ApplyAsync, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>候補を再検証し、durable effectiveを書かず未来の入力だけへ渡します。</summary>
    /// <returns>ローカル実採用の成否です。</returns>
    private async Task<EOutcome> ApplyAsync(NodeValue value, CancellationToken cancellationToken = default)
    {
        StoredWorkflow? candidate = value.Json.Deserialize<StoredWorkflow>();
        if (candidate is null)
        {
            return EOutcome.NotExecuted;
        }
        CompilationResult compiled = compiler.Compile(candidate.Definition);
        if (compiled.Workflow is null || !string.Equals(compiled.Workflow.Revision, candidate.Revision, StringComparison.Ordinal))
        {
            return EOutcome.NotExecuted;
        }
        string savedScope = "workflows";
        string activeScope = "workflow.activation";
        if ((await repository.GetConflictsAsync(savedScope, cancellationToken).ConfigureAwait(false)).Concat(await repository.GetConflictsAsync(activeScope, cancellationToken).ConfigureAwait(false)).Any(entry => string.Equals(entry.Key, candidate.Definition.Id, StringComparison.Ordinal)))
        {
            return EOutcome.NotExecuted;
        }
        StoredWorkflow? marker = (await repository.ReadAsync("workflow.activation", candidate.Definition.Id, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<StoredWorkflow>();
        if (await new SnapshotEditProtection(repository, cluster).GuardAsync(candidate.Definition.Id, marker?.Definition, candidate.Definition, cancellationToken: cancellationToken).ConfigureAwait(false) is null)
        {
            return EOutcome.NotExecuted;
        }
        ConfigurationPhaseResult applied = await activation.ApplyUnsavedAsync(compiled.Workflow, cancellationToken).ConfigureAwait(false);
        if (applied.Outcome == EOutcome.Succeeded)
        {
            protectedMarkers[candidate.Definition.Id] = marker?.Revision;
        }
        return applied.Outcome;
    }

    /// <summary>共有の保存版だけを再確認します。</summary>
    /// <returns>最後に保存した定義です。</returns>
    private async Task<StoredWorkflow?> ReadSavedAsync(string id, CancellationToken cancellationToken = default)
        => (await repository.ReadAsync("workflows", id, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<StoredWorkflow>();

    /// <summary>未作成と保存値を分けて用途キャッシュへ渡します。</summary>
    /// <returns>存在する保存用データです。</returns>
    private static NodeValue? Value(StoredWorkflow? workflow)
    {
        if (workflow is null)
        {
            return null;
        }
        return NodeValue.FromObject(workflow);
    }

    /// <summary>用途内の定義を固定名で識別します。</summary>
    /// <returns>表示と候補検索に共通の識別子です。</returns>
    private static string Dataset(string id) => "workflow:" + id;
}
