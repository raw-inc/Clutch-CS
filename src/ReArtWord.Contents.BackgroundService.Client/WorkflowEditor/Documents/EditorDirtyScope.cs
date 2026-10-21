using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>保存版との差分から未保存ノード集合を編集者情報へ渡します。</summary>
internal static class EditorDirtyScope
{
    /// <summary>配線変更は両端、未確定入力は所有ノードを含めます。</summary>
    /// <returns>nullは範囲不明、空集合はワークフロー属性だけの変更です。</returns>
    internal static IReadOnlyList<string>? Read(EditorDraft draft)
    {
        if (draft.Conflicts.Count != 0 || !string.Equals(draft.Document.Id, draft.Base.Id, StringComparison.Ordinal))
        {
            return null;
        }
        HashSet<string> dirty = new(StringComparer.Ordinal);
        Dictionary<string, NodeDefinition> currentNodes = draft.Document.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Dictionary<string, NodeDefinition> savedNodes = draft.Base.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        string[] ids = currentNodes.Keys.Concat(savedNodes.Keys).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string id in ids)
        {
            NodeDefinition? current = currentNodes.GetValueOrDefault(id);
            NodeDefinition? saved = savedNodes.GetValueOrDefault(id);
            if (!ReferenceEquals(current, saved) && !string.Equals(ContractJson.Hash(current), ContractJson.Hash(saved), StringComparison.Ordinal))
            {
                dirty.Add(id);
            }
        }
        foreach (WireDefinition wire in draft.Document.Wires.Except(draft.Base.Wires).Concat(draft.Base.Wires.Except(draft.Document.Wires)))
        {
            dirty.Add(wire.FromNode);
            dirty.Add(wire.ToNode);
        }
        foreach (string key in draft.InputDrafts?.Keys ?? [])
        {
            string workflowInput = "workflow/traceRawLimit";
            if (string.Equals(key, workflowInput, StringComparison.Ordinal))
            {
                continue;
            }
            string? node = ids.FirstOrDefault(id => key.StartsWith(id + "/settings/", StringComparison.Ordinal) || key.StartsWith(id + "/inputs/", StringComparison.Ordinal));
            if (node is null)
            {
                return null;
            }
            dirty.Add(node);
        }
        return dirty.Order(StringComparer.Ordinal).ToArray();
    }
}
