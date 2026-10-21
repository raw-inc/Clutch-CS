using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>選択ノードと接続の部分置換を保存とGUIで共用します。</summary>
public static class WorkflowSnapshotSelection
{
    /// <summary>現在の対象外ノード・接続線・ワークフロー属性を変えません。</summary>
    /// <returns>部分復元した定義です。</returns>
    public static WorkflowDefinition Patch(WorkflowDefinition current, WorkflowDefinition snapshot, IReadOnlyList<string> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(nodeIds);
        HashSet<string> selected = new(nodeIds, StringComparer.Ordinal);
        Dictionary<string, NodeDefinition> replacements = snapshot.Nodes.Where(node => selected.Contains(node.Id)).ToDictionary(node => node.Id, StringComparer.Ordinal);
        List<NodeDefinition> nodes = [];
        foreach (NodeDefinition node in current.Nodes)
        {
            if (!selected.Contains(node.Id))
            {
                nodes.Add(node);
            }
            else
            {
                if (replacements.Remove(node.Id, out NodeDefinition? replacement))
                {
                    nodes.Add(replacement);
                }
            }
        }
        nodes.AddRange(replacements.Values);
        WireDefinition[] wires = current.Wires.Where(wire => !Touches(wire, selected)).Concat(snapshot.Wires.Where(wire => Touches(wire, selected))).Distinct().ToArray();
        return current with { Nodes = nodes, Wires = wires };
    }

    /// <summary>境界接続も選択ノードの編集範囲に含めます。</summary>
    /// <returns>接続端点が選択されているか。</returns>
    private static bool Touches(WireDefinition wire, HashSet<string> selected) => selected.Contains(wire.FromNode) || selected.Contains(wire.ToNode);
}
