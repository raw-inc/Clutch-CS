using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>折畳みで設定接続の端点を消さないための表示規則です。</summary>
internal static class GraphSettingPorts
{
    /// <summary>上書き用の通常入力と重なる設定口は、保存済み接続がある場合だけ残します。</summary>
    /// <returns>設定行に描画する接続口かです。設定値の編集は引き続き可能です。</returns>
    internal static bool Visible(WorkflowDefinition document, string nodeId, NodeDescriptor descriptor, string port)
    {
        string? property = SettingPorts.Property(port);
        return SettingPorts.IsDeclared(descriptor, port) && (!descriptor.Inputs.Any(input => string.Equals(input.OverridesSetting, property, StringComparison.Ordinal))
            || document.Wires.Any(wire => string.Equals(wire.ToNode, nodeId, StringComparison.Ordinal) && string.Equals(wire.ToPort, port, StringComparison.Ordinal))
            || document.Nodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal))?.Inputs?.ContainsKey(port) == true);
    }

    /// <summary>通常入力欄へ残す設定接続を返します。</summary>
    /// <returns>重複のない接続済みポートです。</returns>
    internal static IReadOnlyList<string> Collapsed(WorkflowDefinition document, string nodeId, NodeDescriptor? descriptor, bool expanded)
    {
        return document.Wires.Where(wire => string.Equals(wire.ToNode, nodeId, StringComparison.Ordinal) && SettingPorts.Property(wire.ToPort) is not null
            && (!expanded || descriptor is null || !SettingPorts.IsDeclared(descriptor, wire.ToPort)))
            .Select(wire => wire.ToPort).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>設定行の描画と線端点で同じ座標を使用します。</summary>
    /// <returns>ノード左上からの縦位置です。</returns>
    internal static double Y(int inputs, int outputs, int index, GraphViewOptions view)
        => 90 + Math.Max(inputs, outputs) * GraphPresentation.PortSpacing(view) + 20 + index * 64 + 32;
}
