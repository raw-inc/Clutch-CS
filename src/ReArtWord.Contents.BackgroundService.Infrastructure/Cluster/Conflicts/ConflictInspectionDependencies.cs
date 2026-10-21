using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Execution;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>ノードの実登録と明示参照から未解消分岐への依存を判定します。</summary>
internal static class ConflictInspectionDependencies
{
    /// <summary>未知・壊れた用途は全体を保留し、既知用途は利用ノードだけを保留します。</summary>
    /// <returns>実開始不可理由またはnullです。</returns>
    internal static string? GetProblem(IReadOnlyList<StateConflict> conflicts, string workflowId, NodeDefinition node, NodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(registry);
        if (conflicts.Any(static conflict => !conflict.Resolvable && !ScheduleConflictInspection.IsScope(conflict.Scope)))
        {
            return "Conflict.UnsupportedScopeOrCandidate";
        }
        if (conflicts.Count == 0)
        {
            return null;
        }
        INode? registered = registry.Find(node.Type);
        if (registered is null)
        {
            return "Conflict.NodeDependencyUnknown";
        }
        foreach (StateConflict conflict in conflicts)
        {
            if (string.Equals(conflict.Scope, NodeStateScope.Cluster, StringComparison.Ordinal))
            {
                string category = "State", setting = "scope", shared = "cluster";
                if (string.Equals(registered.Descriptor.Category, category, StringComparison.Ordinal)
                    && (node.Settings.ValueKind != JsonValueKind.Object || !node.Settings.TryGetProperty(setting, out JsonElement scope)
                        || scope.ValueKind != JsonValueKind.String || string.Equals(scope.GetString(), shared, StringComparison.Ordinal)))
                {
                    return "Conflict.ClusterStateDependency";
                }
                continue;
            }
            if (ScheduleConflictInspection.IsScope(conflict.Scope))
            {
                if (ScheduleConflictInspection.DependsOn(conflict, workflowId, node))
                {
                    return "Conflict.ScheduleDependency";
                }
                continue;
            }
            if (conflict.WorkflowIds.Contains(workflowId, StringComparer.Ordinal))
            {
                if (conflict.Scope.StartsWith(SourcePcAssignments.ScopePrefix, StringComparison.Ordinal))
                {
                    if (string.Equals(conflict.Key, node.Id, StringComparison.Ordinal))
                    {
                        return "Conflict.SourceAssignment";
                    }
                    continue;
                }
                string stateCategory = "State";
                if (!conflict.Scope.StartsWith(ConflictInspection.StatePrefix, StringComparison.Ordinal)
                    || string.Equals(registered.Descriptor.Category, stateCategory, StringComparison.Ordinal))
                {
                    return "Conflict.WorkflowDependency";
                }
            }
            if (string.Equals(conflict.Scope, ConflictInspection.SecretScope, StringComparison.Ordinal) && SecretReference(node, conflict.Key))
            {
                return "Conflict.SecretDependency";
            }
            if (OperationalDependencyPolicy.DependsOn(conflict.OperationalFields, node, registered.Descriptor, registered is IFileFootprintNode))
            {
                return "Conflict.OperationalDependency";
            }
        }
        return null;
    }

    /// <summary>秘密名だけを確認し、値を取得・公開しません。</summary>
    /// <returns>その秘密を参照するか。</returns>
    private static bool SecretReference(NodeDefinition node, string key)
    {
        string property = "secretRef";
        return node.Settings.ValueKind == JsonValueKind.Object && node.Settings.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), key, StringComparison.Ordinal);
    }

}
