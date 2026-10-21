using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Detached;

/// <summary>明示離脱中の設定を共有正本と別のローカル用途へ保存します。</summary>
internal sealed class DetachedWorkflowStore(IDurableRepository repository, PeerCluster cluster, WorkflowCompiler compiler, WorkflowActivation activation)
{
    internal string Scope => "peer:" + cluster.LocalPeerId + ":detached-workflows";

    /// <summary>共有に戻していないローカル版だけを取得します。</summary>
    /// <returns>ローカル枝、なければnullです。</returns>
    internal async Task<DetachedWorkflowRecord?> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(Scope, id, cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<DetachedWorkflowRecord>();
    }

    /// <summary>ローカル枝がなければ最後に確認した共有版を読取りに使います。</summary>
    /// <returns>実際に編集する版です。</returns>
    internal async Task<StoredWorkflow?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        DetachedWorkflowRecord? record = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (record?.Pending == true)
        {
            return record.Local;
        }
        return (await repository.ReadAsync("workflows", id, cancellationToken).ConfigureAwait(false))?.Value?.Json.Deserialize<StoredWorkflow>();
    }

    /// <summary>ローカル保存・ローカルスナップショット成功後だけ新規入力の版を切り替えます。</summary>
    /// <returns>共有成功と区別した各段階です。</returns>
    internal async Task<WorkflowSaveResult> SaveAsync(WorkflowSaveRequest request, CancellationToken cancellationToken = default)
    {
        CompilationResult compiled = compiler.Compile(request.Definition);
        ConfigurationPhaseResult localOnly = new(EOutcome.NotExecuted, "Detached.LocalOnly");
        if (compiled.Workflow is null || string.Equals(request.Definition.Id, ClusterConfigurationStore.Id, StringComparison.Ordinal))
        {
            return new(request.OperationId, null, compiled.Issues, localOnly, localOnly, localOnly, localOnly);
        }
        StoredWorkflow? current = await GetAsync(request.Definition.Id, cancellationToken).ConfigureAwait(false);
        if (current is not null && current.Definition.IsDashboard != request.Definition.IsDashboard)
        {
            return WorkflowDefinitionSaving.KindChangeResult(request.OperationId);
        }
        if (!string.Equals(current?.Revision, request.ExpectedRevision, StringComparison.Ordinal))
        {
            return new(request.OperationId, compiled.Workflow.Revision, [], new(EOutcome.Failed, "Workflow.RevisionConflict"), localOnly, localOnly, localOnly);
        }
        DetachedWorkflowRecord? old = await ReadAsync(request.Definition.Id, cancellationToken).ConfigureAwait(false);
        StoredWorkflow? baseline = current;
        if (old?.Pending == true)
        {
            baseline = old.Base;
        }
        EActivationPolicy policy = WorkflowActivationPolicy.Resolve(activation.GetEffective?.Invoke(request.Definition.Id)?.Definition, request.Definition, request.ActivationPolicy);
        StoredWorkflow local = new(request.Definition, compiled.Workflow.Revision, DateTimeOffset.UtcNow, policy, true);
        SnapshotCaptureResult? before = await new WorkflowBeforeSnapshot(repository, cluster).CaptureAsync(request.OperationId, current,
            request.Definition, activation.GetEffective?.Invoke(request.Definition.Id)?.Definition, true, cancellationToken).ConfigureAwait(false);
        if (before is not null && before.Result.Outcome != EOutcome.Succeeded)
        {
            return new(request.OperationId, local.Revision, [], localOnly, localOnly, localOnly, localOnly, true, before.Result, before.Snapshot?.Id);
        }
        DurableEntry? row = await repository.ReadAsync(Scope, request.Definition.Id, cancellationToken).ConfigureAwait(false);
        string historyScope = "peer:" + cluster.LocalPeerId + ":detached-history:" + request.Definition.Id;
        List<DurableMutation> mutations = [new(Scope, request.Definition.Id, row?.Version, NodeValue.FromObject(new DetachedWorkflowRecord(baseline, local)))];
        if (await repository.ReadAsync(historyScope, local.Revision, cancellationToken).ConfigureAwait(false) is null)
        {
            mutations.Add(new(historyScope, local.Revision, null, NodeValue.FromObject(local)));
        }
        DurableReceipt saved = await repository.ApplyBatchAsync(request.OperationId, mutations, cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult persistence = new(saved.Outcome, saved.Reason);
        if (saved.Outcome != EOutcome.Succeeded)
        {
            return new(request.OperationId, local.Revision, [], persistence, localOnly, localOnly, localOnly, true, before?.Result, before?.Snapshot?.Id);
        }
        string snapshotScope = "peer:" + cluster.LocalPeerId + ":detached-snapshots";
        Guid snapshotId = WorkflowStore.PhaseId(request.OperationId, "snapshot");
        NodeValue snapshotValue = NodeValue.FromObject(new ConfigurationSnapshotState(local with { LocalOnly = false }, activation.GetEffective?.Invoke(local.Definition.Id)?.Definition));
        SnapshotRecord record = new(snapshotId.ToString("N"), local.Definition.Id, cluster.LocalPeerId, ESnapshotKind.Configuration, local.SavedAt, snapshotValue, ContractJson.Hash(snapshotValue), LocalOnly: true);
        DurableReceipt snapshot = await repository.ApplyBatchAsync(snapshotId,
            [new(snapshotScope, record.Id, null, NodeValue.FromObject(record))], cancellationToken).ConfigureAwait(false);
        ConfigurationPhaseResult applied = localOnly;
        if (snapshot.Outcome == EOutcome.Succeeded && policy == EActivationPolicy.Auto)
        {
            applied = await activation.ApplyLocalAsync(local, cancellationToken).ConfigureAwait(false);
        }
        return new(request.OperationId, local.Revision, [], persistence, localOnly, new(snapshot.Outcome, snapshot.Reason), applied, true, before?.Result, before?.Snapshot?.Id);
    }

    /// <summary>共有反映希望を書かず、確認したローカル版だけを明示有効化します。</summary>
    /// <returns>ローカル適用結果です。</returns>
    internal async Task<ConfigurationPhaseResult> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        StoredWorkflow? local = await GetAsync(request.WorkflowId, cancellationToken).ConfigureAwait(false);
        if (local is null || !string.Equals(local.Revision, request.Revision, StringComparison.Ordinal) || (local.ActivationPolicy == EActivationPolicy.ConfirmImpact && !request.ImpactConfirmed))
        {
            return new(EOutcome.NotExecuted, "Activation.ConfirmationOrRevisionMissing");
        }
        DurableEntry? marker = await repository.ReadAsync("workflow.activation", request.WorkflowId, cancellationToken).ConfigureAwait(false);
        if ((local.ActivationPolicy == EActivationPolicy.ConfirmImpact || request.ImpactToken is not null)
            && !string.Equals(request.ImpactToken, WorkflowImpact.Token(local, activation.GetEffective?.Invoke(request.WorkflowId)?.Definition, marker?.Version, cluster.LocalPeerId), StringComparison.Ordinal))
        {
            return new(EOutcome.NotExecuted, "Activation.ImpactChanged");
        }
        return await activation.ApplyLocalAsync(local, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存行を定義一覧へ投影します。</summary>
    /// <returns>定義一覧。</returns>
    internal async Task<IReadOnlyList<StoredWorkflow>> ListAsync(bool includeLocal, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync("workflows", cancellationToken).ConfigureAwait(false);
        Dictionary<string, StoredWorkflow> values = entries.Where(entry => entry.Value is not null).Select(entry => entry.Value!.Json.Deserialize<StoredWorkflow>() ?? throw new InvalidDataException("保存定義が不正です。")).ToDictionary(item => item.Definition.Id, StringComparer.Ordinal);
        if (includeLocal)
        {
            foreach (DurableEntry entry in await repository.ListAsync(Scope, cancellationToken).ConfigureAwait(false))
            {
                DetachedWorkflowRecord? record = entry.Value?.Json.Deserialize<DetachedWorkflowRecord>();
                if (record?.Pending == true)
                {
                    values[entry.Key] = record.Local;
                }
            }
        }
        return values.Values.ToArray();
    }
}
