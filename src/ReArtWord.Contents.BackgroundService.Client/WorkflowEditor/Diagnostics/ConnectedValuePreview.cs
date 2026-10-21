using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>ノードを実行せず、現在の接続から確定できる値だけを編集欄へ表示します。</summary>
internal sealed class ConnectedValuePreview
{
    private readonly EditorDraft draft;
    private readonly WorkflowSettingValues values;
    private readonly ILookup<string, WireDefinition> incoming;
    private readonly Dictionary<(string Node, string Port), NodeValue?> known = [];

    /// <summary>一回の描画で同じ草稿と配線索引を使用します。</summary>
    internal ConnectedValuePreview(EditorDraft draft)
    {
        this.draft = draft;
        values = new(draft.Document);
        incoming = draft.Document.Wires.ToLookup(wire => wire.ToNode, StringComparer.Ordinal);
    }

    /// <summary>無効な入力文字列の追加と解除もプレビューの世代へ含めます。</summary>
    /// <returns>再利用できる草稿かどうかです。</returns>
    internal bool Matches(EditorDraft current) => ReferenceEquals(draft.Document, current.Document) && ReferenceEquals(draft.InputDrafts, current.InputDrafts);

    /// <summary>設定接続の静的解決も同じ文書の索引を使用します。</summary>
    /// <returns>入力欄の型判定用ノードです。</returns>
    internal NodeDefinition Resolve(NodeDefinition node) => values.Resolve(node);

    /// <summary>無効な編集中入力を持つ上流の古い確定値は表示しません。</summary>
    /// <returns>実行せず確定する値です。null値そのものはNodeValue.Nullとして区別します。</returns>
    internal NodeValue? Input(NodeDefinition node, string port)
    {
        if (!known.TryGetValue((node.Id, port), out NodeValue? value))
        {
            value = Read(node, port);
            known[(node.Id, port)] = value;
        }
        return value;
    }

    /// <summary>初回参照時だけ上流の未確定入力を確認します。</summary>
    /// <returns>現在の草稿で確定している値です。</returns>
    private NodeValue? Read(NodeDefinition node, string port)
    {
        string? property = SettingPorts.Property(port);
        string key = node.Id + "/inputs/" + port;
        if (property is not null)
        {
            key = node.Id + "/settings/" + property;
        }
        if (draft.InputDrafts?.ContainsKey(key) == true)
        {
            return null;
        }
        Queue<string> pending = new(incoming[node.Id].Where(wire => string.Equals(wire.ToPort, port, StringComparison.Ordinal)).Select(wire => wire.FromNode));
        HashSet<string> visited = new(StringComparer.Ordinal);
        while (pending.TryDequeue(out string? id))
        {
            if (!visited.Add(id))
            {
                continue;
            }
            string prefix = id + "/";
            if (1024 < visited.Count || draft.InputDrafts?.Keys.Any(input => input.StartsWith(prefix, StringComparison.Ordinal)) == true)
            {
                return null;
            }
            foreach (WireDefinition wire in incoming[id])
            {
                pending.Enqueue(wire.FromNode);
            }
        }
        return values.Input(node, port);
    }

    /// <summary>通常入力による上書きを優先し、保存済みのフォールバックへ戻しません。</summary>
    /// <returns>接続側で確定した値だけです。</returns>
    internal NodeValue? Setting(NodeDefinition node, string property, IReadOnlyList<PortDescriptor> ports)
    {
        PortDescriptor? supplied = ports.FirstOrDefault(port => string.Equals(port.OverridesSetting, property, StringComparison.Ordinal)
            && (node.Inputs?.ContainsKey(port.Name) == true || incoming[node.Id].Any(wire => string.Equals(wire.ToPort, port.Name, StringComparison.Ordinal))));
        return Input(node, supplied?.Name ?? SettingPorts.Name(property));
    }
}
