using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>反復の結果として使用する値の組を、保存済みの入出力から固定します。</summary>
public static class IterationResultPorts
{
    /// <summary>未使用の末尾欄を集約せず、既存のvalueと実際に使用した番号を保持します。</summary>
    /// <returns>番号順の結果ポートです。</returns>
    public static IReadOnlyList<string> Used(NodeDefinition node, WorkflowDefinition document)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(document);
        IEnumerable<string> inputs = document.Wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal)).Select(wire => wire.ToPort);
        IEnumerable<string> outputs = document.Wires.Where(wire => string.Equals(wire.FromNode, node.Id, StringComparison.Ordinal)).Select(wire => PropertyPorts.Parse(wire.FromPort)?.Output ?? wire.FromPort);
        return inputs.Concat(outputs).Concat(node.Inputs?.Keys ?? []).Append(ValuePortPairs.Name(1)).Where(port => 0 < ValuePortPairs.Index(port))
            .Distinct(StringComparer.Ordinal).OrderBy(ValuePortPairs.Index).ToArray();
    }

    /// <summary>未配線の出力だけをnullとし、配線された未到着の結果は補いません。</summary>
    /// <returns>反復終了に必要な未配線値を補った定義です。</returns>
    public static NodeDefinition Configure(NodeDefinition node, WorkflowDefinition document)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(document);
        if (!IterationNodeTypes.IsEnd(node.Type))
        {
            return node;
        }
        HashSet<string> connected = document.Wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal)).Select(wire => wire.ToPort).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, NodeValue> inputs = new(node.Inputs ?? new Dictionary<string, NodeValue>(), StringComparer.Ordinal);
        foreach (string port in Used(node, document).Where(port => !connected.Contains(port)))
        {
            inputs.TryAdd(port, NodeValue.Null);
        }
        return node with { Inputs = inputs };
    }
}
