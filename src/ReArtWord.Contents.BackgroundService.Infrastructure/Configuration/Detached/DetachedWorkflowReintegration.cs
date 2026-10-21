using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Detached;

/// <summary>再参加時のローカル変更を保持し、未観測の共有変更を比較してから通常保存します。</summary>
internal sealed class DetachedWorkflowReintegration(IDurableRepository repository, PeerCluster cluster, DetachedWorkflowStore detached, WorkflowStore workflows)
{
    private readonly ConfigurationOperationJournal journal = new(repository);

    /// <summary>未共有のローカル版と、その時点の共有版を比較します。</summary>
    /// <returns>消さずに保持している変更です。</returns>
    internal async Task<IReadOnlyList<DetachedWorkflowState>> GetAsync(CancellationToken cancellationToken = default)
    {
        List<DetachedWorkflowState> result = [];
        foreach (DurableEntry entry in await repository.ListAsync(detached.Scope, cancellationToken).ConfigureAwait(false))
        {
            DetachedWorkflowRecord? record = entry.Value?.Json.Deserialize<DetachedWorkflowRecord>();
            if (record?.Pending != true)
            {
                continue;
            }
            StoredWorkflow? current = await CurrentAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            WorkflowMergeResult merge = DetachedWorkflowComparison.Merge(record.Base, current, record.Local, Guid.Empty, new Dictionary<string, EEditResolution>(StringComparer.Ordinal));
            WorkflowSaveResult? integration = null;
            if (record.Integration is not null)
            {
                integration = await workflows.GetSaveOperationAsync(record.Integration.OperationId, cancellationToken).ConfigureAwait(false);
            }
            result.Add(new(entry.Key, record.Base, record.Local, current, merge.Conflicts, record.Integration?.OperationId, integration, CanReplaceIntegration(integration)));
        }
        return result;
    }

    /// <summary>非競合だけを通常共有へ渡し、既存統合要求は元結果の照会に限定します。</summary>
    /// <returns>確認と安全な非競合統合の完了です。</returns>
    internal async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!CanIntegrate())
        {
            return;
        }
        foreach (DetachedWorkflowState state in await GetAsync(cancellationToken).ConfigureAwait(false))
        {
            if (state.IntegrationOperationId is not null)
            {
                if (state.Integration?.Shared.Outcome == EOutcome.Succeeded)
                {
                    await CompleteAsync(state.WorkflowId, state.Local.Revision, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            if (state.Conflicts.Count == 0)
            {
                await ResolveAsync(new(Guid.NewGuid(), state.WorkflowId, state.Local.Revision,
                    new Dictionary<string, EEditResolution>(StringComparer.Ordinal), ActivationPolicy: state.Local.ActivationPolicy,
                    ExpectedCurrentRevision: state.Current?.Revision, CurrentRevisionConfirmed: true), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>利用者の比較選択または取消を、確認したローカル版にだけ適用します。</summary>
    /// <returns>競合か、通常保存の段階結果です。</returns>
    internal async Task<WorkflowMergeResult> ResolveAsync(DetachedWorkflowResolution request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ContractJson.Copy(request);
        string kind = "detached.resolve";
        ConfigurationOperation? prior = await journal.ReadAsync(kind, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (!string.Equals(prior.Fingerprint, ContractJson.Hash(request), StringComparison.Ordinal))
            {
                return Rejected("Detached.OperationMismatch");
            }
            return prior.Result?.Deserialize<WorkflowMergeResult>() ?? new(null, [], [], WorkflowStore.UnknownSave(request.OperationId, EOutcome.Unknown, "Detached.OriginalResolutionUnknown"));
        }
        if (request.OperationId == Guid.Empty || !await journal.BeginAsync(kind, request.OperationId, request, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return Rejected("Detached.BeginUnconfirmed");
        }
        try
        {
            WorkflowMergeResult result = await ResolveCoreAsync(request, cancellationToken).ConfigureAwait(false);
            await journal.CompleteAsync(kind, request.OperationId, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            return new(null, [], [], WorkflowStore.UnknownSave(request.OperationId, EOutcome.Unknown, "Detached.ResolutionUnconfirmed"));
        }
    }

    /// <summary>候補を確認した後だけローカル取消または固定した共有要求を実施します。</summary>
    /// <returns>比較と保存の実行結果です。</returns>
    private async Task<WorkflowMergeResult> ResolveCoreAsync(DetachedWorkflowResolution request, CancellationToken cancellationToken = default)
    {
        if (!CanIntegrate())
        {
            return Rejected("Detached.RejoinRequired");
        }
        DurableEntry? entry = await repository.ReadAsync(detached.Scope, request.WorkflowId, cancellationToken).ConfigureAwait(false);
        DetachedWorkflowRecord? record = entry?.Value?.Json.Deserialize<DetachedWorkflowRecord>();
        if (record?.Pending != true || !string.Equals(record.Local.Revision, request.ExpectedLocalRevision, StringComparison.Ordinal))
        {
            return Rejected("Detached.LocalRevisionChanged");
        }
        if (record.Integration is not null)
        {
            WorkflowSaveResult? original = await workflows.GetSaveOperationAsync(record.Integration.OperationId, cancellationToken).ConfigureAwait(false);
            if (!CanReplaceIntegration(original))
            {
                return new(record.Integration.Definition, [], [], original ?? WorkflowStore.UnknownSave(record.Integration.OperationId, EOutcome.Unknown, "Detached.OriginalIntegrationUnknown"));
            }
        }
        StoredWorkflow? current = await CurrentAsync(request.WorkflowId, cancellationToken).ConfigureAwait(false);
        if (!request.Discard && (!request.CurrentRevisionConfirmed || !string.Equals(current?.Revision, request.ExpectedCurrentRevision, StringComparison.Ordinal)))
        {
            return Rejected("Detached.SharedRevisionChanged");
        }
        if (request.Discard)
        {
            string kind = "save";
            if (!await journal.BeginAsync(kind, request.OperationId, request, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return Rejected("Detached.DiscardUnconfirmed");
            }
            await CompleteAsync(request.WorkflowId, record.Local.Revision, cancellationToken).ConfigureAwait(false);
            ConfigurationPhaseResult localOnly = new(EOutcome.NotExecuted, "Detached.Discarded");
            WorkflowSaveResult discarded = new(request.OperationId, current?.Revision, [], new(EOutcome.Succeeded), localOnly, localOnly, localOnly, true);
            await journal.CompleteAsync(kind, request.OperationId, discarded, cancellationToken).ConfigureAwait(false);
            return new(current?.Definition, [], [], discarded);
        }
        WorkflowMergeResult merged = DetachedWorkflowComparison.Merge(record.Base, current, record.Local, request.OperationId, request.Resolutions);
        if (merged.Conflicts.Count != 0 || merged.Candidate is null)
        {
            return merged;
        }
        IReadOnlyList<ValidationIssue> issues = workflows.Validate(merged.Candidate);
        if (issues.Count != 0)
        {
            return merged with { Validation = issues };
        }
        WorkflowSaveRequest save = new(request.OperationId, merged.Candidate, current?.Revision, request.ActivationPolicy);
        DurableReceipt owned = await repository.ApplyBatchAsync(WorkflowStore.PhaseId(request.OperationId, "detached.integration"),
            [new(detached.Scope, request.WorkflowId, entry!.Version, NodeValue.FromObject(record with { Integration = save }))], cancellationToken).ConfigureAwait(false);
        if (owned.Outcome != EOutcome.Succeeded)
        {
            return Rejected("Detached.IntegrationUnconfirmed");
        }
        WorkflowSaveResult result = await workflows.SaveAsync(save, cancellationToken).ConfigureAwait(false);
        if (result.Shared.Outcome == EOutcome.Succeeded)
        {
            await CompleteAsync(request.WorkflowId, record.Local.Revision, cancellationToken).ConfigureAwait(false);
        }
        return merged with { Save = result };
    }

    /// <summary>ローカル版の明示取消・共有完了だけを記録し、元情報は消しません。</summary>
    /// <returns>ローカル状態の更新完了です。</returns>
    private async Task CompleteAsync(string id, string revision, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(detached.Scope, id, cancellationToken).ConfigureAwait(false);
        DetachedWorkflowRecord? record = entry?.Value?.Json.Deserialize<DetachedWorkflowRecord>();
        if (record is null || !string.Equals(record.Local.Revision, revision, StringComparison.Ordinal))
        {
            throw new InvalidDataException("局所版が変更されています。");
        }
        DurableReceipt completed = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(detached.Scope, id, entry!.Version, NodeValue.FromObject(record with { Pending = false }))], cancellationToken).ConfigureAwait(false);
        if (completed.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidDataException("局所変更の完了を確認できません。");
        }
    }

    /// <summary>ローカル優先Getを使わず、現在の共有正本を読みます。</summary>
    /// <returns>共有定義です。</returns>
    private async Task<StoredWorkflow?> CurrentAsync(string id, CancellationToken cancellationToken = default)
        => (await repository.ReadAsync("workflows", id, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<StoredWorkflow>();

    /// <summary>明示再参加と同期確認を経由するまで共有を書きません。</summary>
    /// <returns>通常共有を開始できるか。</returns>
    private bool CanIntegrate() => !cluster.IsDetached && cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status));

    /// <summary>一部保存済み・副作用済み・不明を、新しい要求の許可へ読み替えません。</summary>
    /// <returns>元要求が変更前で終了したと確認できるかです。</returns>
    private static bool CanReplaceIntegration(WorkflowSaveResult? result) => result is not null
        && result.Saved.Outcome is EOutcome.Failed or EOutcome.NotExecuted
        && result.Shared.Outcome is EOutcome.Failed or EOutcome.NotExecuted
        && result.Snapshot.Outcome == EOutcome.NotExecuted && result.Activation.Outcome == EOutcome.NotExecuted;

    /// <summary>共有不能を成功表示しません。</summary>
    /// <returns>利用者判断が必要な結果です。</returns>
    private static WorkflowMergeResult Rejected(string reason) => new(null, [], [new ValidationIssue(reason, "局所変更を保持したまま、現在状態を確認してください。")], null);
}
