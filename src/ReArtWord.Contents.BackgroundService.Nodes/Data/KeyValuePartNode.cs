using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>Key-Valueノードまたは辞書反復の一組から、キー・値を独立して取得します。</summary>
public sealed class KeyValuePartNode : PureValueNode
{
    private readonly bool key;

    /// <summary>取得する側だけが異なる二つのノードを公開します。</summary>
    public KeyValuePartNode(bool key)
    {
        this.key = key;
        string type = "data.keyValue.value", name = "Get value from Key-Value";
        if (key)
        {
            type = "data.keyValue.key";
            name = "Get key from Key-Value";
        }
        Descriptor = NodeSupport.Describe(type, name, "Data", """{"type":"object","properties":{}}""",
            [new("value", EPortValueKind.Json, true) { DataType = new("object") }], [new("value", EPortValueKind.Json)]) with
            { Description = "Extract the key or value from exactly one Key-Value entry, including an item produced by iterating a dictionary." };
    }

    /// <summary>一組の入力と、型を引き継ぐ出力です。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>複数キーの辞書は曖昧に選択せずエラーにします。</summary>
    /// <returns>キーまたは値の独立した複製です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
        => KeyValueParts.Read(inputs.GetValueOrDefault("value"), key);
}
