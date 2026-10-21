using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>開始信号の合流口と、保存版で固定する接続集合を共有します。</summary>
public static class SignalMergePorts
{
    /// <summary>受信ごと・一巡の先着・全入力の三方式です。</summary>
    /// <returns>開始信号の合流ノードかです。</returns>
    public static bool IsNode(string type) => type is "control.signal.each" or "control.signal.first" or "control.signal.all";

    /// <summary>先着は同じ要求・反復内で合流し、明示した場合だけ要求をまたぎます。</summary>
    /// <returns>新しい入力事象へ切り離さない先着合流かです。</returns>
    public static bool IsScopedFirst(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string first = "control.signal.first", scope = "scope", events = "events";
        return string.Equals(node.Type, first, StringComparison.Ordinal)
            && !(node.Settings.TryGetProperty(scope, out JsonElement value) && value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), events, StringComparison.Ordinal));
    }

    /// <summary>都度・全入力・明示した要求横断の先着だけを別事象として配送します。</summary>
    /// <returns>共有の信号受付を使用するノードかです。</returns>
    public static bool IsEventBoundary(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return IsNode(node.Type) && !IsScopedFirst(node);
    }

    /// <summary>Concatと同じ番号付き入力の識別子を使用します。</summary>
    /// <returns>入力番号、対象外は0です。</returns>
    public static int Index(string port) => ConcatPorts.Index(port);

    /// <summary>実行時に待つ入力集合を、未接続の追加口とは区別します。</summary>
    /// <returns>この保存版だけの接続集合を持つノードです。</returns>
    public static NodeDefinition Configure(NodeDefinition node, WorkflowDefinition document)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(document);
        if (!IsNode(node.Type))
        {
            return node;
        }
        string[] names = document.Wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal) && 0 < Index(wire.ToPort))
            .Select(wire => wire.ToPort).Distinct(StringComparer.Ordinal).OrderBy(Index).ToArray();
        JsonObject settings = JsonNode.Parse(node.Settings.GetRawText())?.AsObject() ?? new();
        settings["$signalInputs"] = JsonSerializer.SerializeToNode(names);
        return node with { Settings = JsonSerializer.SerializeToElement(settings) };
    }
}
