using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>一つの変更されないグラフの接続検査で、推論とポート解決を共有します。別の編集版や並列検査には共有しません。</summary>
public sealed class WorkflowConnectionInspection
{
    private readonly Dictionary<string, NodeDefinition> nodes;
    private readonly Dictionary<string, NodeDescriptor> descriptors;
    private readonly Dictionary<string, NodeDescriptor?> ports = new(StringComparer.Ordinal);
    private readonly Lazy<WorkflowTypeInference> inference;

    /// <summary>対象の定義とカタログを、この検査一回の寿命へ固定します。</summary>
    public WorkflowConnectionInspection(WorkflowDefinition document, IReadOnlyList<NodeDescriptor> catalog)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        Document = document;
        Settings = new(document);
        nodes = document.Nodes.Where(node => node is not null && !string.IsNullOrEmpty(node.Id) && !string.IsNullOrEmpty(node.Type)).GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Settings.Resolve(group.First()), StringComparer.Ordinal);
        descriptors = catalog.GroupBy(node => node.Type, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        inference = new(() => new(document, catalog));
        Incoming = document.Wires.Where(wire => wire is not null).ToLookup(wire => wire.ToNode, StringComparer.Ordinal);
    }

    /// <summary>この検査が扱う一つのグラフです。</summary>
    internal WorkflowDefinition Document
    {
        get;
    }

    /// <summary>接続された固定設定値の共通解釈です。</summary>
    internal WorkflowSettingValues Settings
    {
        get;
    }

    /// <summary>接続型の検査に使う全カタログの推論です。</summary>
    internal WorkflowTypeInference Inference => inference.Value;

    /// <summary>設定入力の依存を毎回全配線から再構築しません。</summary>
    internal ILookup<string, WireDefinition> Incoming
    {
        get;
    }

    /// <summary>設定接続を反映したノードを識別子で取得します。</summary>
    /// <returns>見つからない場合はnullです。</returns>
    internal NodeDefinition? FindNode(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        return nodes.GetValueOrDefault(id);
    }

    /// <summary>元カタログの能力宣言を取得します。</summary>
    /// <returns>未登録の種類ならnullです。</returns>
    internal NodeDescriptor? FindDescriptor(string type) => descriptors.GetValueOrDefault(type);

    /// <summary>同じノードのポート形状は一度だけ解決し、従来の単一種類によるプロパティ推論を維持します。</summary>
    /// <returns>この設定版のポート契約です。</returns>
    public NodeDescriptor? GetPorts(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        if (ports.TryGetValue(nodeId, out NodeDescriptor? known))
        {
            return known;
        }
        NodeDefinition? node = FindNode(nodeId);
        if (node is null)
        {
            return null;
        }
        NodeDescriptor? descriptor = FindDescriptor(node.Type);
        if (descriptor is null)
        {
            return null;
        }
        NodeDescriptor? resolved = ConfiguredNodePorts.Resolve(descriptor, node, Document, sharedInference: Inference);
        ports.Add(nodeId, resolved);
        return resolved;
    }
}
