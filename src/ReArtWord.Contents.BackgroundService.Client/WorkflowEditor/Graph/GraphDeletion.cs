using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>削除操作を既存の変更履歴と復元ロックへ一つに接続します。</summary>
internal static class GraphDeletion
{
    /// <summary>関連する線も含めて削除できるか確認します。</summary>
    /// <returns>削除可能な場合はtrueです。</returns>
    internal static bool CanNode(EditorDocument model, string id)
        => CanNodes(model, new HashSet<string>([id], StringComparer.Ordinal));

    /// <summary>複数ノードと接続先の保護をまとめて検証し、部分削除を許しません。</summary>
    /// <returns>対象が存在し、全体を削除できる場合はtrueです。</returns>
    internal static bool CanNodes(EditorDocument model, IReadOnlySet<string> ids)
        => ids.Count != 0 && ids.All(id => model.Draft.Document.Nodes.Any(node => string.Equals(node.Id, id, StringComparison.Ordinal)))
            && model.Locks.Allows(model.Draft.Document, WithoutNodes(model.Draft.Document, ids));

    /// <summary>両端のロックを含めて配線の削除を確認します。</summary>
    /// <returns>削除可能な場合はtrueです。</returns>
    internal static bool CanWire(EditorDocument model, int index)
        => 0 <= index && index < model.Draft.Document.Wires.Count && model.Locks.Allows(model.Draft.Document, WithoutWire(model.Draft.Document, index));

    /// <summary>ノードと付随する線を一回のUndo対象として削除します。</summary>
    /// <returns>削除した場合はtrueです。</returns>
    internal static bool Node(EditorDocument model, string id)
        => Nodes(model, new HashSet<string>([id], StringComparer.Ordinal));

    /// <summary>選択範囲と関連線を一回の変更として削除し、一回のUndoで復元可能にします。</summary>
    /// <returns>全対象を削除した場合だけtrueです。</returns>
    internal static bool Nodes(EditorDocument model, IReadOnlySet<string> ids)
    {
        if (!CanNodes(model, ids))
        {
            return false;
        }
        WorkflowDefinition next = WithoutNodes(model.Draft.Document, ids);
        model.Change(_ => next);
        model.SelectedNode = null;
        model.SelectedWire = null;
        return true;
    }

    /// <summary>線の削除を既存の編集履歴へ記録します。</summary>
    /// <returns>削除した場合はtrueです。</returns>
    internal static bool Wire(EditorDocument model, int index)
    {
        if (!CanWire(model, index))
        {
            return false;
        }
        model.Change(document => WithoutWire(document, index));
        model.SelectedWire = null;
        return true;
    }

    /// <summary>確認と適用で同じ削除対象を使います。</summary>
    /// <returns>ノードと接続を除いた定義です。</returns>
    private static WorkflowDefinition WithoutNodes(WorkflowDefinition document, IReadOnlySet<string> ids)
        => document with { Nodes = document.Nodes.Where(node => !ids.Contains(node.Id)).ToArray(), Wires = document.Wires.Where(wire => !ids.Contains(wire.FromNode) && !ids.Contains(wire.ToNode)).ToArray() };

    /// <summary>指定した線だけを除きます。</summary>
    /// <returns>削除後の定義です。</returns>
    private static WorkflowDefinition WithoutWire(WorkflowDefinition document, int index)
        => document with { Wires = document.Wires.Where((_, position) => position != index).ToArray() };
}
