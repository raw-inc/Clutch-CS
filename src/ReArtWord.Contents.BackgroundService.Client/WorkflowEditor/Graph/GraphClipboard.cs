using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>選択範囲とその内部配線だけを独立したコピーとして保持します。</summary>
internal sealed class GraphClipboard
{
    private WorkflowDefinition? content;

    /// <summary>ノードを一つ以上コピー済みかどうかです。</summary>
    internal bool HasContent => 0 < content?.Nodes.Count;

    /// <summary>元文書の後の編集に影響されないコピーを作ります。</summary>
    internal void Copy(WorkflowDefinition document, IReadOnlySet<string> selection)
    {
        NodeDefinition[] nodes = document.Nodes.Where(node => selection.Contains(node.Id)).ToArray();
        content = ContractJson.Copy(document with { Nodes = nodes, Wires = document.Wires.Where(wire => selection.Contains(wire.FromNode) && selection.Contains(wire.ToNode)).ToArray() });
    }

    /// <summary>最小X/Yをマウス位置へ合わせ、衝突しないIDで一括追加します。</summary>
    /// <returns>貼り付けられたノード識別子です。</returns>
    internal IReadOnlyList<string> Paste(EditorDocument model, NodePosition position)
    {
        if (content is null || content.Nodes.Count == 0 || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
        {
            return [];
        }
        WorkflowDefinition current = model.Draft.Document;
        WorkflowDefinition copied = Clone(content, new(Math.Max(0, position.X), Math.Max(0, position.Y)));
        WorkflowDefinition next = current with { Nodes = [.. current.Nodes, .. copied.Nodes], Wires = [.. current.Wires, .. copied.Wires] };
        if (!model.Locks.Allows(current, next))
        {
            return [];
        }
        model.Change(_ => next);
        model.SelectedWire = null;
        model.Selection.Single(null);
        model.Selection.Add(copied.Nodes.Select(node => node.Id));
        return copied.Nodes.Select(node => node.Id).ToArray();
    }

    /// <summary>設定・入力・サイズ・配線条件を深くコピーして相対位置を維持します。</summary>
    /// <returns>複製済み部分グラフです。</returns>
    internal static WorkflowDefinition Clone(WorkflowDefinition source, NodePosition position)
    {
        WorkflowDefinition copy = ContractJson.Copy(source);
        double left = copy.Nodes.Min(node => (node.Position ?? new(60, 80)).X), top = copy.Nodes.Min(node => (node.Position ?? new(60, 80)).Y);
        Dictionary<string, string> ids = copy.Nodes.ToDictionary(node => node.Id, _ => "node-" + Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
        return copy with
        {
            Nodes = copy.Nodes.Select(node => node with
            {
                Id = ids[node.Id],
                Position = new(position.X + (node.Position ?? new(60, 80)).X - left, position.Y + (node.Position ?? new(60, 80)).Y - top),
            }).ToArray(),
            Wires = copy.Wires.Select(wire => wire with { FromNode = ids[wire.FromNode], ToNode = ids[wire.ToNode] }).ToArray(),
        };
    }
}
