using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>永続分岐の公開可能な候補と依存範囲を検査します。</summary>
internal static class ConflictInspection
{
    internal static string StatePrefix => "workflow:";
    internal static string HistoryPrefix => "workflow.history:";
    internal static string SecretScope => "secrets";
    private static readonly HashSet<string> workflowScopes = new(["workflows", "workflow.activation"], StringComparer.Ordinal);
    private static readonly HashSet<string> guardScopes = new(["snapshot.edit-locks", "snapshot.configuration-locks"], StringComparer.Ordinal);

    /// <summary>同じ対象の全候補を比較し、秘密値を公開用DTOから除外します。</summary>
    /// <returns>版と用途を保持した候補集合です。</returns>
    internal static StateConflict Describe(IReadOnlyList<DurableEntry> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("候補が必要です。", nameof(candidates));
        }
        DurableEntry first = candidates[0];
        if (candidates.Any(candidate => !string.Equals(candidate.Scope, first.Scope, StringComparison.Ordinal) || !string.Equals(candidate.Key, first.Key, StringComparison.Ordinal)))
        {
            throw new ArgumentException("同じscopeとkeyの候補が必要です。", nameof(candidates));
        }
        bool hidden = string.Equals(first.Scope, SecretScope, StringComparison.Ordinal);
        if (ScheduleConflictInspection.IsScope(first.Scope))
        {
            return ScheduleConflictInspection.Describe(candidates);
        }
        if (string.Equals(first.Scope, NodeStateScope.Cluster, StringComparison.Ordinal))
        {
            StateConflictCandidate[] states = candidates.Select(candidate => new StateConflictCandidate(candidate.Version, candidate.Value?.Copy())).ToArray();
            return new(ContractJson.Hash(new { first.Scope, first.Key }), first.Scope, first.Key, states, [], [], false, candidates.All(static candidate => ValidValue(candidate.Value)));
        }
        bool resolvable = false;
        string[] workflows = [];
        IReadOnlyList<string> fields = [];
        try
        {
            if (first.Scope.StartsWith(SourcePcAssignments.ScopePrefix, StringComparison.Ordinal))
            {
                string workflow = first.Scope[SourcePcAssignments.ScopePrefix.Length..];
                workflows = [workflow];
                resolvable = !string.IsNullOrWhiteSpace(workflow) && candidates.All(candidate => candidate.Value is null
                    || candidate.Value.Json.Deserialize<SourcePcAssignment>(ContractJson.Options) is { } assignment && SourcePcAssignments.Valid(assignment, workflow, first.Key));
            }
            else
            {
                if (workflowScopes.Contains(first.Scope) || first.Scope.StartsWith(HistoryPrefix, StringComparison.Ordinal))
                {
                    string id = first.Key;
                    if (first.Scope.StartsWith(HistoryPrefix, StringComparison.Ordinal))
                    {
                        id = first.Scope[HistoryPrefix.Length..];
                    }
                    workflows = [id];
                    resolvable = !string.IsNullOrWhiteSpace(id) && candidates.All(candidate => ValidWorkflow(candidate.Value, id));
                }
                else
                {
                    if (first.Scope.StartsWith(StatePrefix, StringComparison.Ordinal))
                    {
                        workflows = [first.Scope[StatePrefix.Length..]];
                        resolvable = !string.IsNullOrWhiteSpace(workflows[0]) && candidates.All(static candidate => ValidValue(candidate.Value));
                    }
                    else
                    {
                        if (hidden)
                        {
                            resolvable = candidates.All(static candidate => candidate.Value is null || candidate.Value.Kind == EValueKind.Json && candidate.Value.Json.ValueKind == JsonValueKind.String);
                        }
                        else
                        {
                            if (ConflictInspectionOperational.IsScope(first.Scope))
                            {
                                fields = ConflictInspectionOperational.Differences(candidates);
                                resolvable = true;
                            }
                            else
                            {
                                resolvable = guardScopes.Contains(first.Scope) && candidates.All(static candidate => ValidGuard(candidate.Value))
                                    && candidates.Select(static candidate => ContractJson.Hash(candidate.Value)).Distinct(StringComparer.Ordinal).Count() == 1;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            resolvable = false;
        }
        StateConflictCandidate[] values = candidates.Select(candidate =>
        {
            NodeValue? value = null;
            if (!hidden && ValidValue(candidate.Value))
            {
                value = candidate.Value?.Copy();
            }
            return new StateConflictCandidate(candidate.Version, value);
        }).ToArray();
        return new StateConflict(ContractJson.Hash(new { first.Scope, first.Key }), first.Scope, first.Key, values, workflows, fields, hidden, resolvable);
    }

    /// <summary>開始するノードが依存する用途だけを隔離します。</summary>
    /// <returns>依存競合の理由またはnullです。</returns>
    internal static string? GetExecutionProblem(IReadOnlyList<StateConflict> conflicts, string workflowId, NodeDefinition node, NodeRegistry registry)
        => ConflictInspectionDependencies.GetProblem(conflicts, workflowId, node, registry);

    /// <summary>保存定義の型と内容識別子を確認します。</summary>
    /// <returns>解消候補として検証可能か。</returns>
    private static bool ValidWorkflow(NodeValue? value, string id)
    {
        if (value is null)
        {
            return true;
        }
        if (value.Kind != EValueKind.Json)
        {
            return false;
        }
        StoredWorkflow? stored = value.Json.Deserialize<StoredWorkflow>(ContractJson.Options);
        return stored is not null && stored.Definition.Nodes is not null && stored.Definition.Wires is not null
            && string.Equals(stored.Definition.Id, id, StringComparison.Ordinal)
            && string.Equals(stored.Revision, ContractJson.Hash(stored.Definition), StringComparison.Ordinal);
    }

    /// <summary>同値の復元保護条件でも型が壊れた候補を採用しません。</summary>
    /// <returns>既知の保護条件配列か削除であるか。</returns>
    private static bool ValidGuard(NodeValue? value)
    {
        if (value is null)
        {
            return true;
        }
        return value.Kind == EValueKind.Json && value.Json.Deserialize<SnapshotEditLock[]>(ContractJson.Options) is not null;
    }

    /// <summary>状態値の区分が壊れていないか確認します。</summary>
    /// <returns>値を比較可能か。</returns>
    private static bool ValidValue(NodeValue? value) => value is null || value.Kind == EValueKind.Binary || value.Kind == EValueKind.Json && value.Json.ValueKind != JsonValueKind.Undefined;
}
