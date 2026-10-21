using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>ボタンの値を入力と出力で対にし、旧ポートと既存配線の番号を維持します。</summary>
public static class ButtonValuePorts
{
    /// <summary>明示押下で値を渡すボタンの識別子です。</summary>
    public static string NodeType => "control.button";

    /// <summary>最初の値は保存済みワークフローの識別子を維持します。</summary>
    /// <returns>入出力に共通する値の識別子です。</returns>
    public static string Name(int index) => ValuePortPairs.Name(index);

    /// <summary>番号の範囲と表記を他の可変ポートと揃えます。</summary>
    /// <returns>値の番号、または対象外なら0です。</returns>
    public static int Index(string name) => ValuePortPairs.Index(name);

    /// <summary>入力済みなら次の一組を表示し、出力だけ接続された既存の組も残します。</summary>
    /// <returns>途中の空欄を詰めない入出力の表示数です。</returns>
    public static int VisibleCount(IEnumerable<string> supplied, IEnumerable<string> connectedOutputs) => ValuePortPairs.VisibleCount(supplied, connectedOutputs);

    /// <summary>接続された空欄はnullとして渡し、未使用の最大64口を実行履歴へ増やしません。</summary>
    /// <returns>この保存版の接続だけに未指定値を補った実行定義です。</returns>
    public static NodeDefinition Configure(NodeDefinition node, WorkflowDefinition document)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(node.Type, NodeType, StringComparison.Ordinal))
        {
            return node;
        }
        IEnumerable<string> incoming = document.Wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal)).Select(wire => wire.ToPort);
        IEnumerable<string> outgoing = document.Wires.Where(wire => string.Equals(wire.FromNode, node.Id, StringComparison.Ordinal)).Select(wire => PropertyPorts.Parse(wire.FromPort)?.Output ?? wire.FromPort);
        Dictionary<string, NodeValue> inputs = new(node.Inputs ?? new Dictionary<string, NodeValue>(), StringComparer.Ordinal);
        foreach (string port in incoming.Concat(outgoing).Where(port => 0 < Index(port)))
        {
            inputs.TryAdd(port, NodeValue.Null);
        }
        return node with { Inputs = inputs };
    }
}
