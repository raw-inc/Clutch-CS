using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

/// <summary>操作画面の参照と配置の保存形を検証し、実行ノードを一つも生成しません。</summary>
internal static class WfDashboardCompiler
{
    /// <summary>元WFの削除は参照不明として表示できるよう、保存時に外部処理を呼びません。</summary>
    /// <returns>実行順が空の検証済み定義、または保存できない理由です。</returns>
    internal static CompilationResult Compile(WorkflowDefinition definition)
    {
        List<ValidationIssue> issues = [];
        if (string.IsNullOrWhiteSpace(definition.Id) || definition.Nodes is null || definition.Wires is null)
        {
            return new(null, [new("dashboard.required", "WFDashboard requires an identifier and card list.")]);
        }
        if (definition.Enabled || definition.IsRecipe || definition.Wires.Count != 0 || 128 < definition.Nodes.Count)
        {
            issues.Add(new("dashboard.structure", "WFDashboard supports up to 128 cards and does not support workflow execution or connections."));
        }
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (NodeDefinition node in definition.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id))
            {
                issues.Add(new("dashboard.identity", "Each card requires a unique identifier."));
                continue;
            }
            ValidateSettings(definition.Id, node, issues);
            if (node.StartOnLoad || node.Inputs is { Count: > 0 } || node.Target is not null || node.Safety is not null
                || node.Position is { } position && (!double.IsFinite(position.X) || !double.IsFinite(position.Y)))
            {
                issues.Add(new("dashboard.card", "WFDashboard cards cannot contain executable inputs or execution targets.", node.Id));
            }
        }
        if (issues.Count != 0)
        {
            return new(null, issues);
        }
        WorkflowDefinition copied = ContractJson.Copy(definition);
        return new(new(copied, ContractJson.Hash(copied), [], new Dictionary<string, IReadOnlyList<WireDefinition>>(StringComparer.Ordinal)), []);
    }

    /// <summary>ローカルの配置と、別WFの参照を区別して設定だけを検証します。</summary>
    private static void ValidateSettings(string dashboardId, NodeDefinition node, List<ValidationIssue> issues)
    {
        if (string.Equals(node.Type, WfDashboardStateReference.CardType, StringComparison.Ordinal))
        {
            if (WfDashboardStateReference.Read(node) is null)
            {
                issues.Add(new("dashboard.state", "Select a state scope, workflow and key.", node.Id));
            }
            return;
        }
        if (string.Equals(node.Type, WfDashboardLayout.CommentType, StringComparison.Ordinal))
        {
            if (!WfDashboardLayout.TryReadComment(node.Settings, out _))
            {
                issues.Add(new("dashboard.comment", "Enter a comment of up to 16384 characters.", node.Id));
            }
            return;
        }
        if (string.Equals(node.Type, WfDashboardLayout.BreakType, StringComparison.Ordinal))
        {
            if (node.Settings.ValueKind != JsonValueKind.Object || node.Settings.GetPropertyCount() != 0)
            {
                issues.Add(new("dashboard.break", "A line break requires an empty settings object.", node.Id));
            }
            return;
        }
        WfDashboardReference? reference = null;
        try
        {
            reference = WfDashboardReference.Read(node);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            issues.Add(new("dashboard.reference", "The card reference could not be read.", node.Id));
        }
        if (reference is null || string.IsNullOrWhiteSpace(reference.WorkflowId) || string.IsNullOrWhiteSpace(reference.NodeId)
            || !WfDashboardReference.Supports(reference.NodeType) || string.Equals(reference.WorkflowId, dashboardId, StringComparison.Ordinal))
        {
            issues.Add(new("dashboard.reference", "Select a supported node in another workflow in this cluster.", node.Id));
        }
    }
}
