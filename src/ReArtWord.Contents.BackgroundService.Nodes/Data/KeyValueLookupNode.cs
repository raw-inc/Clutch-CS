using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>キーで値を取り出し、見つからない場合だけ既定値を返します。</summary>
public sealed class KeyValueLookupNode : PureValueNode
{
    /// <summary>配列、キー、任意型の既定値を入力として公開します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(KeyValueLookup.NodeType, "Get value by key", "Data", KeyValueLookup.SettingsSchema,
        inputs: [new("value", EPortValueKind.Json, true),
            new("key", EPortValueKind.Json, true) { DataType = new("string") }, new("defaultValue")],
        outputs: [new("value")]) with
        {
            Description = "Look up a case-sensitive key in an array of property maps or key/value pairs. Return the default only when the key is absent; omitted defaults return null."
        };

    /// <summary>選択値と既定値の型・Path情報を保持し、文字列を勝手に数値化しません。</summary>
    /// <returns>選択値、既定値、または未指定時のnullです。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        string format = new NodeSettings(definition.Settings).ReadText("format") ?? "auto";
        return KeyValueLookup.Find(inputs.GetValueOrDefault("value"), inputs.GetValueOrDefault("key"), format)
            ?? inputs.GetValueOrDefault("defaultValue")?.Copy() ?? NodeValue.Null;
    }
}
