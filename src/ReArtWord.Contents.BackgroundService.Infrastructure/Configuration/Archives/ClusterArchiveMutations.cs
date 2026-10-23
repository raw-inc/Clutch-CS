using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>設定、反映する版、復元前のスナップショットを一括保存するための変更データを作成します。</summary>
internal sealed class ClusterArchiveMutations(IDurableRepository repository, PeerCluster cluster)
{
    /// <summary>編集保護を含む変更データを作成します。保存時に版の一致を検証し、ほかの編集による変更の上書きを防ぎます。</summary>
    /// <returns>一括保存する変更データ。</returns>
    internal async Task<IReadOnlyList<DurableMutation>> CreateAsync(Guid operation, ClusterArchiveConfiguration before,
        ClusterArchiveConfiguration incoming, EArchiveImportMode mode, CancellationToken cancellationToken = default)
    {
        List<DurableMutation> mutations = [];
        SnapshotEditProtection protection = new(repository, cluster);
        foreach (ArchivedWorkflow workflow in incoming.Workflows)
        {
            string id = workflow.Saved.Definition.Id;
            ArchivedWorkflow? previous = before.Workflows.FirstOrDefault(item => string.Equals(item.Saved.Definition.Id, id, StringComparison.Ordinal));
            DurableMutation? guard = await protection.GuardAsync(id, previous?.Saved.Definition, workflow.Saved.Definition, operation, cancellationToken).ConfigureAwait(false);
            mutations.Add(guard ?? throw new InvalidOperationException("Archive workflow protection changed."));
            await SetAsync(mutations, "workflows", id, NodeValue.FromObject(workflow.Saved), cancellationToken).ConfigureAwait(false);
            await SetAsync(mutations, "workflow.activation", id, Effective(workflow), cancellationToken).ConfigureAwait(false);
            await HistoryAsync(mutations, "workflow.history:" + id, workflow.Saved.Revision, NodeValue.FromObject(workflow.Saved), cancellationToken).ConfigureAwait(false);
            AddWorkflowSnapshot(mutations, operation, "imported", workflow);
            if (previous is not null)
            {
                AddWorkflowSnapshot(mutations, operation, "before", previous);
            }
            if (workflow.Effective is not null && !string.Equals(workflow.Saved.Revision, ContractJson.Hash(workflow.Effective), StringComparison.Ordinal))
            {
                StoredWorkflow effective = new(workflow.Effective, ContractJson.Hash(workflow.Effective), incoming.CapturedAt, EActivationPolicy.Explicit);
                await HistoryAsync(mutations, "workflow.history:" + id, effective.Revision, NodeValue.FromObject(effective), cancellationToken).ConfigureAwait(false);
                AddWorkflowSnapshot(mutations, operation, "effective", new(effective, workflow.Effective));
            }
        }
        if (mode == EArchiveImportMode.Replace)
        {
            HashSet<string> kept = incoming.Workflows.Select(item => item.Saved.Definition.Id).ToHashSet(StringComparer.Ordinal);
            foreach (ArchivedWorkflow workflow in before.Workflows.Where(item => !kept.Contains(item.Saved.Definition.Id)))
            {
                string id = workflow.Saved.Definition.Id;
                DurableMutation? guard = await protection.GuardAsync(id, workflow.Saved.Definition, null, operation, cancellationToken).ConfigureAwait(false);
                mutations.Add(guard ?? throw new InvalidOperationException("Archive deletion protection changed."));
                await SetAsync(mutations, "workflows", id, null, cancellationToken).ConfigureAwait(false);
                await SetAsync(mutations, "workflow.activation", id, null, cancellationToken).ConfigureAwait(false);
                AddWorkflowSnapshot(mutations, operation, "before", workflow);
            }
        }
        DurableMutation? commonGuard = await protection.GuardConfigurationAsync(ClusterConfigurationStore.Id, operation, cancellationToken).ConfigureAwait(false);
        mutations.Add(commonGuard ?? throw new InvalidOperationException("Archive configuration protection changed."));
        ClusterOperationalState common = incoming.Operational;
        NodeValue? saved = null, active = null;
        if (common.Saved is not null)
        {
            saved = NodeValue.FromObject(new ClusterConfigurationStored(common.Saved, common.SavedActivationPolicy));
            await HistoryAsync(mutations, ClusterConfigurationStore.HistoryScope, common.Saved.Revision, saved, cancellationToken).ConfigureAwait(false);
        }
        if (common.Effective is not null)
        {
            active = NodeValue.FromObject(common.Effective);
        }
        await SetAsync(mutations, ClusterConfigurationStore.SavedScope, ClusterConfigurationStore.Id, saved, cancellationToken).ConfigureAwait(false);
        await SetAsync(mutations, ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, active, cancellationToken).ConfigureAwait(false);
        AddSnapshot(mutations, operation, "imported-common", ClusterConfigurationStore.Id, NodeValue.FromObject(common));
        if (common.Effective is not null && !string.Equals(common.Saved?.Revision, common.Effective.Revision, StringComparison.Ordinal))
        {
            ClusterOperationalState effective = new(common.Effective, common.Effective, EActivationPolicy.Explicit);
            AddSnapshot(mutations, operation, "effective-common", ClusterConfigurationStore.Id, NodeValue.FromObject(effective));
        }
        AddSnapshot(mutations, operation, "before-common", ClusterConfigurationStore.Id, NodeValue.FromObject(before.Operational));
        return mutations;
    }

    /// <summary>現在の版を保存時の照合条件として、変更データを追加します。</summary>
    /// <returns>変更データの追加処理。</returns>
    private async Task SetAsync(List<DurableMutation> mutations, string scope, string key, NodeValue? value, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        mutations.Add(new(scope, key, entry?.Version, value));
    }

    /// <summary>履歴にない版だけを追加します。</summary>
    /// <returns>履歴の変更データを追加する処理。</returns>
    private async Task HistoryAsync(List<DurableMutation> mutations, string scope, string revision, NodeValue value, CancellationToken cancellationToken = default)
    {
        DurableEntry? existing = await repository.ReadAsync(scope, revision, cancellationToken).ConfigureAwait(false);
        if (existing?.Value is null)
        {
            mutations.Add(new(scope, revision, existing?.Version, value));
        }
    }

    /// <summary>保存版と有効版を含むスナップショットを追加します。</summary>
    private void AddWorkflowSnapshot(List<DurableMutation> mutations, Guid operation, string phase, ArchivedWorkflow workflow)
        => AddSnapshot(mutations, operation, phase, workflow.Saved.Definition.Id, NodeValue.FromObject(new ConfigurationSnapshotState(workflow.Saved, workflow.Effective)));

    /// <summary>復元の操作IDから生成したIDで、スナップショットを追加します。</summary>
    private void AddSnapshot(List<DurableMutation> mutations, Guid operation, string phase, string target, NodeValue value)
    {
        string id = WorkflowStore.PhaseId(operation, "archive:" + phase + ":" + target).ToString("N");
        SnapshotRecord snapshot = new(id, target, cluster.LocalPeerId, ESnapshotKind.Configuration, DateTimeOffset.UtcNow, value, ContractJson.Hash(value));
        mutations.Add(new("snapshots", id, null, NodeValue.FromObject(snapshot)));
    }

    /// <summary>有効だった版を反映用の保存データに変換します。有効版がなければnullにします。</summary>
    /// <returns>反映する版の保存データ。</returns>
    private static NodeValue? Effective(ArchivedWorkflow workflow)
    {
        if (workflow.Effective is null)
        {
            return null;
        }
        return NodeValue.FromObject(new StoredWorkflow(workflow.Effective, ContractJson.Hash(workflow.Effective), workflow.Saved.SavedAt, EActivationPolicy.Explicit));
    }
}
