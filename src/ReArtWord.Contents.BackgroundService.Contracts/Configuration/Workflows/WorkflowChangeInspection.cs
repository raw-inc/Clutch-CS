using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>ノードの実行設定を表示情報と分けて比較します。</summary>
public static class WorkflowChangeInspection
{
    /// <summary>既に保存済みでも未反映の差分を含めて、有効版から変更種類を求めます。</summary>
    /// <returns>既定の順序で重複しない変更種類です。</returns>
    public static IReadOnlyList<EWorkflowChangeKind> Kinds(WorkflowDefinition? before, WorkflowDefinition after)
    {
        ArgumentNullException.ThrowIfNull(after);
        HashSet<EWorkflowChangeKind> changes = [];
        if (before is null || !Same(before.Name, after.Name))
        {
            changes.Add(EWorkflowChangeKind.Appearance);
        }
        if (before is null || before.IsDashboard != after.IsDashboard || !Same(before.Wires, after.Wires))
        {
            changes.Add(EWorkflowChangeKind.Structure);
        }
        if (before is null || before.Enabled != after.Enabled)
        {
            changes.Add(EWorkflowChangeKind.Execution);
        }
        if (before is null || !Same(new { before.Trace, before.TraceSelection, before.ContainsPersonalData }, new { after.Trace, after.TraceSelection, after.ContainsPersonalData }))
        {
            changes.Add(EWorkflowChangeKind.Diagnostics);
        }
        if (!Same(before?.ActivationRules, after.ActivationRules))
        {
            changes.Add(EWorkflowChangeKind.Policy);
        }
        Dictionary<string, NodeDefinition> oldNodes = before?.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        Dictionary<string, NodeDefinition> newNodes = after.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (string id in oldNodes.Keys.Union(newNodes.Keys, StringComparer.Ordinal))
        {
            InspectNode(oldNodes.GetValueOrDefault(id), newNodes.GetValueOrDefault(id), changes);
        }
        return changes.Order().ToArray();
    }

    /// <summary>各ノードの実行設定と表示変更を分類します。</summary>
    private static void InspectNode(NodeDefinition? before, NodeDefinition? after, HashSet<EWorkflowChangeKind> changes)
    {
        if (before is null || after is null)
        {
            changes.UnionWith([EWorkflowChangeKind.Structure, EWorkflowChangeKind.Values, EWorkflowChangeKind.Execution]);
            return;
        }
        if (!Same(new { before.Type, before.ContractVersion }, new { after.Type, after.ContractVersion }))
        {
            changes.Add(EWorkflowChangeKind.Structure);
        }
        if (!Same(new { before.Settings, before.Inputs }, new { after.Settings, after.Inputs }))
        {
            changes.Add(EWorkflowChangeKind.Values);
        }
        if (!Same(new { before.Target, before.PcExecution, before.Safety, before.StartOnLoad, before.ListenDurationMs, before.Overlap },
            new { after.Target, after.PcExecution, after.Safety, after.StartOnLoad, after.ListenDurationMs, after.Overlap }))
        {
            changes.Add(EWorkflowChangeKind.Execution);
        }
        if (!Same(before.Position, after.Position) || !Same(before.Size, after.Size) || !Same(before.LogicalName, after.LogicalName))
        {
            changes.Add(EWorkflowChangeKind.Appearance);
        }
    }

    /// <summary>項目順によらず値を比較します。</summary>
    /// <returns>同じ意味のJSON表現であるかです。</returns>
    private static bool Same<T>(T before, T after) => string.Equals(ContractJson.Hash(before), ContractJson.Hash(after), StringComparison.Ordinal);

    /// <summary>追加・削除を含むノード設定変更を識別します。表示情報と列挙順は含めません。</summary>
    /// <returns>実行設定が変わったノードの識別子です。</returns>
    public static IReadOnlyList<string> ChangedNodes(WorkflowDefinition before, WorkflowDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        Dictionary<string, NodeDefinition> previous = before.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        Dictionary<string, NodeDefinition> current = after.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        return previous.Keys.Union(current.Keys, StringComparer.Ordinal).Where(id =>
            !previous.TryGetValue(id, out NodeDefinition? old) || !current.TryGetValue(id, out NodeDefinition? next)
            || !string.Equals(ContractJson.Hash(old with { Position = null, Size = null, LogicalName = null }), ContractJson.Hash(next with { Position = null, Size = null, LogicalName = null }), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
    }
}
