using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>カードと詳細欄の定数入力を同じ草稿境界で更新します。</summary>
internal static class NodeInputEditing
{
    /// <summary>接続候補の型・復元ロック・置換対象を、草稿やUndoを変更せず検査します。</summary>
    /// <param name="model">編集状態。</param>
    /// <param name="wire">新しい接続。</param>
    /// <param name="catalog">判明している型契約です。</param>
    /// <param name="expectedDocument">操作開始時の草稿です。途中の変更があれば確定しません。</param>
    /// <returns>現在の文書へ接続を追加できるか。</returns>
    public static bool CanConnect(EditorDocument model, WireDefinition wire, IReadOnlyList<NodeDescriptor>? catalog = null, WorkflowDefinition? expectedDocument = null)
    {
        WorkflowDefinition document = model.Draft.Document;
        NodeDefinition? destination = document.Nodes.FirstOrDefault(node => string.Equals(node.Id, wire.ToNode, StringComparison.Ordinal));
        NodeDescriptor? descriptor = catalog?.FirstOrDefault(node => string.Equals(node.Type, destination?.Type, StringComparison.Ordinal));
        bool multiple = PcSelectionPorts.AllowsMultiple(descriptor, wire.ToPort);
        if ((expectedDocument is not null && !ReferenceEquals(expectedDocument, document))
            || string.Equals(wire.FromNode, wire.ToNode, StringComparison.Ordinal)
            || !document.Nodes.Any(node => string.Equals(node.Id, wire.ToNode, StringComparison.Ordinal))
            || !document.Nodes.Any(node => string.Equals(node.Id, wire.FromNode, StringComparison.Ordinal))
            || model.Locks.Node(document.Id, wire.FromNode) || model.Locks.Node(document.Id, wire.ToNode)
            || document.Wires.Contains(wire)
            || (!multiple && document.Wires.Any(item => Occupies(item, wire) && model.Locks.Node(document.Id, item.FromNode)))
            || !GraphConnectionPolicy.CanConnect(document, catalog ?? [], wire))
        {
            return false;
        }
        return true;
    }

    /// <summary>定数から接続への変更を一回のUndo単位で確定します。</summary>
    /// <returns>接続を追加したか。</returns>
    public static bool Connect(EditorDocument model, WireDefinition wire, IReadOnlyList<NodeDescriptor>? catalog = null, WorkflowDefinition? expectedDocument = null)
    {
        if (!CanConnect(model, wire, catalog, expectedDocument))
        {
            return false;
        }
        NodeDefinition destination = model.Draft.Document.Nodes.First(node => string.Equals(node.Id, wire.ToNode, StringComparison.Ordinal));
        NodeDescriptor? descriptor = catalog?.FirstOrDefault(node => string.Equals(node.Type, destination.Type, StringComparison.Ordinal));
        bool multiple = PcSelectionPorts.AllowsMultiple(descriptor, wire.ToPort);
        model.Change(current => current with
        {
            Nodes = current.Nodes.Select(node =>
            {
                if (!string.Equals(node.Id, wire.ToNode, StringComparison.Ordinal) || node.Inputs is null)
                {
                    return node;
                }
                Dictionary<string, NodeValue> inputs = node.Inputs.ToDictionary();
                inputs.Remove(wire.ToPort);
                return node with { Inputs = inputs };
            }).ToArray(),
            Wires = [..current.Wires.Where(item => multiple || !Occupies(item, wire)), wire],
        });
        string key = wire.ToNode + "/inputs/" + wire.ToPort;
        string? property = SettingPorts.Property(wire.ToPort);
        if (property is not null)
        {
            key = wire.ToNode + "/settings/" + property;
        }
        if (model.ReadInput(key) is not null)
        {
            model.SetInput(key, null);
        }
        return true;
    }

    /// <summary>置換先入力を一意に識別します。</summary>
    /// <returns>同じ入力へ接続しているかです。</returns>
    private static bool Occupies(WireDefinition existing, WireDefinition candidate) => string.Equals(existing.ToNode, candidate.ToNode, StringComparison.Ordinal) && string.Equals(existing.ToPort, candidate.ToPort, StringComparison.Ordinal);

    /// <summary>接続・復元lockを再検証し、不正な入力だけを未確定草稿へ保持します。</summary>
    /// <param name="model">編集状態。</param>
    /// <param name="nodeId">対象ノード。</param>
    /// <param name="port">入力名。</param>
    /// <param name="raw">入力文字列。</param>
    /// <param name="binary">Base64入力か。</param>
    /// <param name="kind">JSONの編集型。</param>
    public static void Commit(EditorDocument model, string nodeId, string port, string raw, bool binary, JsonValueKind kind)
    {
        NodeDefinition? node = model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        if (node is null || model.Locks.Node(model.Draft.Document.Id, nodeId)
            || model.Draft.Document.Wires.Any(wire => string.Equals(wire.ToNode, nodeId, StringComparison.Ordinal) && string.Equals(wire.ToPort, port, StringComparison.Ordinal)))
        {
            return;
        }
        string key = nodeId + "/inputs/" + port;
        try
        {
            Dictionary<string, NodeValue> inputs = node.Inputs?.ToDictionary() ?? [];
            if (raw.Length == 0)
            {
                inputs.Remove(port);
            }
            else
            {
                if (binary)
                {
                    inputs[port] = NodeValue.FromBytes(Convert.FromBase64String(raw));
                }
                else
                {
                    NodeValue parsed = Parse(raw, kind);
                    if (node.Inputs?.GetValueOrDefault(port)?.DataType is { } semantic)
                    {
                        parsed = Contracts.Values.PathTypeMetadata.Apply(parsed, semantic);
                    }
                    string definition = "type definition";
                    if (string.Equals(Contracts.Values.BuiltinInputTypes.Expected(node.Type, port)?.Type, definition, StringComparison.Ordinal)
                        && Contracts.Values.ValueTypeInference.Descriptor(parsed) is null)
                    {
                        throw new FormatException("A type definition is required.");
                    }
                    inputs[port] = parsed;
                }
            }
            model.ChangeNode(nodeId, current => current with { Inputs = inputs });
            if (model.ReadInput(key) is not null)
            {
                model.SetInput(key, null);
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            model.SetInput(key, raw);
        }
    }

    /// <summary>文字列以外はJSON数値精度を保持して型を検証します。</summary>
    /// <param name="raw">入力文字列。</param>
    /// <param name="kind">編集型。</param>
    /// <returns>確定した値。</returns>
    private static NodeValue Parse(string raw, JsonValueKind kind)
    {
        if (kind == JsonValueKind.String)
        {
            return NodeValue.FromJson(JsonSerializer.SerializeToElement(raw));
        }
        using JsonDocument document = JsonDocument.Parse(raw);
        if ((kind == JsonValueKind.Number && document.RootElement.ValueKind != JsonValueKind.Number)
            || (kind is JsonValueKind.True or JsonValueKind.False && document.RootElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
        {
            throw UiError.Attach(new FormatException("The input type does not match."), "The input type does not match.");
        }
        return NodeValue.FromJson(document.RootElement);
    }
}
