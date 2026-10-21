using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>競合解消でも通常のスナップショット編集保護を同じ原子更新へ含めます。</summary>
internal static class ConflictResolutionChanges
{
    /// <summary>新内容による変更範囲を通常保存と同じ規則で確認します。</summary>
    /// <returns>保護を含む変更、復元中ならnullです。</returns>
    internal static async Task<IReadOnlyList<DurableMutation>?> CreateAsync(StateConflict conflict, NodeValue? value, IReadOnlyList<string> versions,
        IDurableRepository repository, PeerCluster cluster, CancellationToken cancellationToken = default)
    {
        List<DurableMutation> changes = [new(conflict.Scope, conflict.Key, null, value, versions.Order(StringComparer.Ordinal).ToArray())];
        SnapshotEditProtection protection = new(repository, cluster);
        bool definition = conflict.Scope is "workflows" or "workflow.activation";
        if (definition)
        {
            DurableEntry? current = await repository.ReadAsync(conflict.Scope, conflict.Key, cancellationToken).ConfigureAwait(false);
            StoredWorkflow? old = current?.Value?.Json.Deserialize<StoredWorkflow>();
            StoredWorkflow? next = value?.Json.Deserialize<StoredWorkflow>();
            DurableMutation? guard = await protection.GuardAsync(conflict.Key, old?.Definition, next?.Definition, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (guard is null)
            {
                return null;
            }
            changes.Add(guard);
        }
        if (ConflictCandidateValidation.IsOperational(conflict.Scope))
        {
            DurableMutation? guard = await protection.GuardConfigurationAsync(ClusterConfigurationStore.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (guard is null)
            {
                return null;
            }
            changes.Add(guard);
        }
        return changes;
    }
}
