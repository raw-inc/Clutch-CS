using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;

/// <summary>宣言と接続から成功出力の型だけを推測します。ノードや外部処理は実行しません。</summary>
internal sealed class GraphTypeInference
{
    private readonly WorkflowDefinition document;
    private readonly NodeDescriptor[] catalog;
    private readonly Dictionary<string, NodeDefinition> nodes;
    private readonly Dictionary<string, NodeDescriptor> types;
    private readonly Dictionary<string, NodeDescriptor?> descriptors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeDescriptor?> editable = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Node, string Port, bool Outbound), GraphPortPresentation> ports = [];
    private readonly Dictionary<WireDefinition, GraphPortPresentation> wires = [];
    private readonly WorkflowSettingValues settings;
    private readonly WorkflowTypeInference inference;
    private static string AnyLabel => "port.type.any";

    /// <summary>共通の型推測を表示へ接続します。</summary>
    internal GraphTypeInference(WorkflowDefinition document, IReadOnlyList<NodeDescriptor> descriptors)
    {
        this.document = document;
        catalog = descriptors.ToArray();
        settings = new(document);
        nodes = document.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => settings.Resolve(group.First()), StringComparer.Ordinal);
        types = catalog.GroupBy(node => node.Type, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        inference = new(document, descriptors);
    }

    /// <summary>文書とカタログの同じ版にだけ解析結果を再利用します。</summary>
    /// <returns>可変のカタログ一覧は要素の置換も検出します。</returns>
    internal bool Matches(WorkflowDefinition current, IReadOnlyList<NodeDescriptor> available) => ReferenceEquals(document, current) && catalog.SequenceEqual(available);

    /// <summary>現在版のノードを配線やポートの数によらず索引で取得します。</summary>
    /// <returns>接続設定を解決した表示用ノードです。</returns>
    internal NodeDefinition? Node(string id) => nodes.GetValueOrDefault(id);

    /// <summary>同じノードの動的なポート一覧を一度だけ構築します。</summary>
    /// <returns>保存された配線を含む契約です。</returns>
    internal NodeDescriptor? Descriptor(string id)
    {
        if (!descriptors.TryGetValue(id, out NodeDescriptor? descriptor))
        {
            NodeDefinition? node = Node(id);
            descriptor = ConfiguredNodePorts.Resolve(types.GetValueOrDefault(node?.Type ?? string.Empty), node, document, inference);
            descriptors[id] = descriptor;
        }
        return descriptor;
    }

    /// <summary>カードと配線端点で同一の編集用ポートと設定を共有します。</summary>
    /// <returns>表示する項目だけに絞った定義です。</returns>
    internal NodeDescriptor? EditorDescriptor(string id)
    {
        if (!editable.TryGetValue(id, out NodeDescriptor? descriptor))
        {
            descriptor = EditorNodeDescriptor.Editable(Descriptor(id), Node(id), document, inference);
            editable[id] = descriptor;
        }
        return descriptor;
    }

    /// <summary>型検証の契約を変更せず、設定と接続から分かる値の種類を表示します。</summary>
    /// <returns>宣言を保持した推測結果です。</returns>
    internal GraphPortPresentation Port(string nodeId, string port, bool outbound)
    {
        if (ports.TryGetValue((nodeId, port, outbound), out GraphPortPresentation? cached))
        {
            return cached;
        }
        NodeDefinition? node = Node(nodeId);
        NodeDescriptor? descriptor = Descriptor(nodeId);
        GraphPortPresentation presentation = GraphPresentation.Port(descriptor, port, outbound);
        ValueTypeDefinition? type = inference.PortType(nodeId, port, outbound);
        if (!outbound && node is not null)
        {
            type = descriptor?.Inputs.FirstOrDefault(input => string.Equals(input.Name, port, StringComparison.Ordinal))?.DataType
                ?? BuiltinInputTypes.Expected(node.Type, port) ?? type;
        }
        string propertiesKey = "properties";
        string typeKey = "type";
        if (!outbound && SettingPorts.Property(port) is { } property && descriptor?.SettingsSchema.ValueKind == JsonValueKind.Object
            && descriptor.SettingsSchema.TryGetProperty(propertiesKey, out JsonElement properties) && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(property, out JsonElement schema) && schema.ValueKind == JsonValueKind.Object)
        {
            presentation = new(port, "any", AnyLabel, "Setting input");
            if (type is null && schema.TryGetProperty(typeKey, out JsonElement declared) && declared.ValueKind == JsonValueKind.String && declared.GetString() is { } name && ValueTypeInference.KnownName(name))
            {
                type = new(name);
            }
        }
        return ports[(nodeId, port, outbound)] = presentation.WithType(type);
    }

    /// <summary>抽出後に型が確認できない線を、元の型で表示しません。</summary>
    /// <returns>宣言を保持した推測結果です。</returns>
    internal GraphPortPresentation Wire(WireDefinition wire)
    {
        if (wires.TryGetValue(wire, out GraphPortPresentation? cached))
        {
            return cached;
        }
        GraphPortPresentation presentation = GraphPresentation.Wire(Descriptor(wire.FromNode), wire);
        ValueTypeDefinition? inferred = inference.OutputType(wire);
        if (wire.Path is not null && inferred is not null)
        {
            presentation = new(wire.FromPort, "any", AnyLabel, "The type is inferred from the connected value; runtime validation still applies.");
        }
        return wires[wire] = presentation.WithType(inferred);
    }

    /// <summary>接続元だけを調べ、接続先の旧配線を受入契約に使いません。</summary>
    /// <returns>安全に分かる出力型、または未確定です。</returns>
    internal ValueTypeDefinition? OutputType(WireDefinition wire) => inference.OutputType(wire);

}
