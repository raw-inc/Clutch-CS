using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>Key-Value配列または既存辞書から、重複方針付きの辞書を作成します。</summary>
public sealed class DictionaryValueNode : PureValueNode
{
    /// <summary>Arrayと同じ一括入力と追加項目を持ち、空辞書も作成できます。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(DictionaryValues.NodeType, "Build dictionary", "Values", DictionaryValues.SettingsSchema,
        inputs: [new("items", EPortValueKind.Json), new("item", EPortValueKind.Json) { DataType = new("object") }], outputs: [new("value", EPortValueKind.Json)]) with
        { Description = "Build a dictionary from Key and value nodes, an array of property maps, or an existing dictionary. Duplicate keys are rejected or replaced with the last value." };

    /// <summary>同じキーの衝突と、型・値の組を共通規則で処理します。</summary>
    /// <returns>元の入力とは独立した辞書です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        NodeValue value = DictionaryValues.Build(inputs.GetValueOrDefault("items"), inputs.GetValueOrDefault("item"), new NodeSettings(definition.Settings).ReadText("duplicate", "reject")!);
        ValueTypeOperations.ValidateValue(value, new("object"));
        return value;
    }
}
