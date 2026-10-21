using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>接続識別子は維持し、コメントノードの表示名だけを上流へ解決します。</summary>
internal static class GraphPortNames
{
    /// <summary>空の別名は元のポート名を引き継ぎ、不正な循環は既定名へ戻します。</summary>
    /// <returns>翻訳済みのポート名、または利用者が指定した名前です。</returns>
    internal static string Resolve(WorkflowDefinition document, UiText ui, string nodeId, string port, bool outbound)
    {
        string commentType = "value.comment", valuePort = "value", expandType = "data.object.expand", regexType = "text.regex";
        NodeDefinition? first = document.Nodes.FirstOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        if (first is not null && SubworkflowInterface.IsBoundary(first.Type) && string.Equals(port, valuePort, StringComparison.Ordinal))
        {
            string nameKey = "name";
            if (first.Settings.ValueKind == System.Text.Json.JsonValueKind.Object && first.Settings.TryGetProperty(nameKey, out System.Text.Json.JsonElement name)
                && name.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                return name.GetString()!;
            }
        }
        if (outbound && PropertyPorts.Parse(port) is { } field)
        {
            if (string.Equals(first?.Type, regexType, StringComparison.Ordinal))
            {
                string full = "0";
                return string.Equals(field.Property, full, StringComparison.Ordinal) ? ui["Regex full match"]
                                                                                     : field.Property;
            }
            return (IterationNodeTypes.IsStart(first?.Type ?? string.Empty) || string.Equals(first?.Type, expandType, StringComparison.Ordinal)) ? field.Property
                                                                                                                                                 : ui.PortName(first?.Type, field.Output, true) + " · " + field.Property;
        }
        if (!outbound || !string.Equals(first?.Type, commentType, StringComparison.Ordinal) || !string.Equals(port, valuePort, StringComparison.Ordinal))
        {
            return ui.PortName(first?.Type, port, outbound);
        }
        HashSet<string> visited = new(StringComparer.Ordinal);
        WorkflowSettingValues values = new(document);
        while (visited.Count < 64 && visited.Add(nodeId))
        {
            NodeDefinition? node = document.Nodes.FirstOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
            if (node is null || !string.Equals(node.Type, commentType, StringComparison.Ordinal) || !string.Equals(port, valuePort, StringComparison.Ordinal) || !outbound)
            {
                return ui.PortName(node?.Type, port, outbound);
            }
            NodeValue? name = values.Input(node, "displayName");
            bool supplied = document.Wires.Any(wire => string.Equals(wire.ToNode, nodeId, StringComparison.Ordinal) && wire.ToPort is "displayName")
                || node.Inputs?.ContainsKey("displayName") == true;
            if (name is null && supplied)
            {
                return ui["Dynamic output name"];
            }
            if (name?.Kind == EValueKind.Json && name.Json.ValueKind == System.Text.Json.JsonValueKind.String && name.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
            WireDefinition[] incoming = document.Wires.Where(wire => string.Equals(wire.ToNode, nodeId, StringComparison.Ordinal) && wire.ToPort is "value").Take(2).ToArray();
            if (incoming.Length != 1)
            {
                return ui.PortName(node.Type, port, true);
            }
            nodeId = incoming[0].FromNode;
            port = incoming[0].FromPort;
        }
        return ui.PortName(null, port, outbound);
    }
}
