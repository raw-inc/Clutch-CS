using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Contracts.Targets;

/// <summary>PC選択の専用ポートを、編集画面と実行前検証で共通に扱います。</summary>
public static class PcSelectionPorts
{
    /// <summary>JSONの配列と区別して中継するPC選択の型名です。</summary>
    public static string TypeName => "pc-selection";

    /// <summary>PC一覧を選択するノードの種類です。</summary>
    public static string SelectNode => "execution.pc.select";

    /// <summary>リソースを比較するノードの種類です。</summary>
    public static string LowestNode => "execution.pc.lowest";

    /// <summary>各処理ノードの実行PC入力です。</summary>
    public static string Execution => "$execution.pc";

    /// <summary>低負荷PC選択の比較対象入力です。</summary>
    public static string Candidates => "candidates";

    /// <summary>同じ入力へ複数のPC選択を接続できるか確認します。</summary>
    /// <returns>実行PC入力またはPC選択型の入力ならtrue。</returns>
    public static bool AllowsMultiple(NodeDescriptor? descriptor, string port)
        => string.Equals(port, Execution, StringComparison.Ordinal)
            || descriptor?.Inputs.Any(input => string.Equals(input.Name, port, StringComparison.Ordinal) && input.ValueKind == EPortValueKind.PcSelection) == true;

    /// <summary>PC選択を通常データへ変換せず中継できる入力・出力を判定します。</summary>
    /// <returns>値中継、ボタンの値入出力、または値合流の番号付き入力ならtrue。</returns>
    public static bool IsRelay(NodeDefinition node, string port)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Type is "value.comment" or "subworkflow.input" or "subworkflow.output" or "subworkflow.port" or "control.concurrencyLimit" or "control.concurrencyLimit.end" && port is "value"
            || string.Equals(node.Type, ButtonValuePorts.NodeType, StringComparison.Ordinal) && 0 < ButtonValuePorts.Index(port)
            || ValueMergePorts.IsNode(node.Type) && (port is "value" || 0 < ConcatPorts.Index(port));
    }

    /// <summary>すべての接続候補がPC選択である合流を、通常値と区別します。</summary>
    /// <returns>通常の値イベントとは区別されたPC候補の合流かです。</returns>
    public static bool IsMerge(NodeDefinition node, WorkflowDefinition document, WorkflowTypeInference? inference = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(document);
        return ValueMergePorts.IsNode(node.Type) && string.Equals((inference ?? new(document, [])).PortType(node.Id, "value", true)?.Type, TypeName, StringComparison.Ordinal);
    }

    /// <summary>Comment、ボタンの対応する値入力、設定時のValueMergeからPC候補を追跡します。</summary>
    /// <returns>元の出力ノード。不完全な接続、抽出、失敗経路、循環ではnull。</returns>
    public static NodeDefinition? Origin(WorkflowDefinition document, WireDefinition wire)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(wire);
        HashSet<string> visited = new(StringComparer.Ordinal);
        string success = "success", any = "*", value = "value";
        while (visited.Count < 64 && wire.Path is null && (string.Equals(wire.When, success, StringComparison.Ordinal) || string.Equals(wire.When, any, StringComparison.Ordinal)) && visited.Add(wire.FromNode))
        {
            NodeDefinition? node = document.Nodes.FirstOrDefault(item => string.Equals(item.Id, wire.FromNode, StringComparison.Ordinal));
            string trigger = "trigger";
            if (node is not null && document.Wires.Any(item => string.Equals(item.ToNode, node.Id, StringComparison.Ordinal) && string.Equals(item.ToPort, trigger, StringComparison.Ordinal)))
            {
                return null;
            }
            if (node is null || !IsRelay(node, wire.FromPort))
            {
                return string.Equals(wire.FromPort, value, StringComparison.Ordinal) ? node
                                                                                     : null;
            }
            IEnumerable<WireDefinition> candidates = document.Wires.Where(item => string.Equals(item.ToNode, node.Id, StringComparison.Ordinal) && IsRelay(node, item.ToPort));
            string inputPort = value;
            if (string.Equals(node.Type, ButtonValuePorts.NodeType, StringComparison.Ordinal))
            {
                inputPort = wire.FromPort;
                candidates = candidates.Where(item => string.Equals(item.ToPort, inputPort, StringComparison.Ordinal));
            }
            if (ValueMergePorts.IsNode(node.Type))
            {
                candidates = candidates.OrderBy(item => ConcatPorts.Index(item.ToPort)).Take(1);
            }
            WireDefinition[] inputs = candidates.Take(2).ToArray();
            if (inputs.Length != 1 || node.Inputs?.ContainsKey(inputPort) == true)
            {
                return null;
            }
            wire = inputs[0];
        }
        return null;
    }
}
