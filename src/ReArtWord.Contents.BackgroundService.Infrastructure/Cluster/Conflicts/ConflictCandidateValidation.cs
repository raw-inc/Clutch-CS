using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Conflicts;

/// <summary>人が選んだ値にも通常保存と同じ対象・意味・内容版を要求します。</summary>
internal static class ConflictCandidateValidation
{
    /// <summary>用途に応じて候補を検証し、未知形式を有効な共有値と推測しません。</summary>
    /// <returns>開始前に解消する問題です。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(StateConflict conflict, NodeValue? value, WorkflowCompiler compiler)
    {
        if (!conflict.Resolvable)
        {
            return [new("conflict.unsupported", "この用途は通常の候補選択だけで整合を確認できません。")];
        }
        if (ScheduleConflictInspection.IsScope(conflict.Scope))
        {
            return ScheduleConflictInspection.Validate(conflict, value);
        }
        if (value is null)
        {
            return [];
        }
        try
        {
            if (conflict.Scope.StartsWith(SourcePcAssignments.ScopePrefix, StringComparison.Ordinal))
            {
                SourcePcAssignment? assignment = value.Json.Deserialize<SourcePcAssignment>(ContractJson.Options);
                if (assignment is null || !SourcePcAssignments.Valid(assignment, conflict.Scope[SourcePcAssignments.ScopePrefix.Length..], conflict.Key))
                {
                    return [new("conflict.sourceAssignment", "待受の担当PCと対象Workflow・ノードを確認できません。")];
                }
                return [];
            }
            if (IsWorkflow(conflict.Scope))
            {
                StoredWorkflow workflow = value.Json.Deserialize<StoredWorkflow>() ?? throw new JsonException();
                string id = conflict.Key;
                string history = "workflow.history:";
                if (conflict.Scope.StartsWith(history, StringComparison.Ordinal))
                {
                    id = conflict.Scope[history.Length..];
                }
                CompilationResult result = compiler.Compile(workflow.Definition);
                if (result.Workflow is null)
                {
                    return result.Issues;
                }
                if (!string.Equals(id, workflow.Definition.Id, StringComparison.Ordinal) || !string.Equals(workflow.Revision, result.Workflow.Revision, StringComparison.Ordinal))
                {
                    return [new("conflict.definitionMismatch", "候補の対象または内容版が一致しません。")];
                }
            }
            if (IsOperational(conflict.Scope))
            {
                ClusterOperationalSnapshot? snapshot = Operational(conflict.Scope, value);
                if (snapshot is null || !string.Equals(snapshot.Revision, ContractJson.Hash(snapshot.Settings), StringComparison.Ordinal))
                {
                    return [new("conflict.operationalMismatch", "共通設定の内容版を確認できません。")];
                }
                return ClusterConfigurationValidation.Validate(snapshot.Settings);
            }
            return [];
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return [new("conflict.invalidCandidate", "候補の型または必要項目が不正です。")];
        }
    }

    /// <summary>ワークフロー保存・反映希望・履歴に同じ定義検証を使います。</summary>
    /// <returns>定義を持つ用途か。</returns>
    internal static bool IsWorkflow(string scope) => scope is "workflows" or "workflow.activation" || scope.StartsWith("workflow.history:", StringComparison.Ordinal);

    /// <summary>共通運用設定の用途を判定します。</summary>
    /// <returns>共通設定を持つ用途か。</returns>
    internal static bool IsOperational(string scope) => scope is "cluster.operational" or "cluster.operational.activation" or "cluster.operational.history";

    /// <summary>保存候補と反映希望の外側のデータ構造だけを分けます。</summary>
    /// <returns>設定の内容版です。</returns>
    internal static ClusterOperationalSnapshot? Operational(string scope, NodeValue value)
    {
        string activation = "cluster.operational.activation";
        if (string.Equals(scope, activation, StringComparison.Ordinal))
        {
            return value.Json.Deserialize<ClusterOperationalSnapshot>();
        }
        return value.Json.Deserialize<ClusterConfigurationStored>()?.Snapshot;
    }

    /// <summary>時刻メタデータだけの差を設定内容の分岐としません。</summary>
    /// <returns>内容の意味を比較する識別子です。</returns>
    internal static string Meaning(DurableEntry entry)
    {
        if (entry.Value is not null && IsWorkflow(entry.Scope))
        {
            StoredWorkflow? workflow = entry.Value.Json.Deserialize<StoredWorkflow>();
            if (workflow is not null)
            {
                return ContractJson.Hash(new { workflow.Definition, workflow.Revision, workflow.ActivationPolicy });
            }
        }
        return ContractJson.Hash(entry.Value);
    }
}
