using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;

/// <summary>配列または辞書から、指定した0始まりの添字の要素を取得します。</summary>
public sealed class PickNode : PureValueNode
{
    /// <summary>配列または辞書と整数Indexを受け取り、要素の型を維持して返します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.pick", "Pick collection item", "Data", """{"type":"object","properties":{}}""",
        inputs: [new("value", EPortValueKind.Json, true), new("index", EPortValueKind.Json, true) { DataType = new("integer") }],
        outputs: [new("value", EPortValueKind.Json)]) with
        {
            IsValueOperation = true,
            Description = "Select one array item or dictionary key-value pair by its zero-based Index. Preserve values and types, including null. Empty collections, fractional or out-of-range indices fail. Dictionary order is not guaranteed."
        };

    /// <summary>指定Indexを丸めず、配列要素または辞書の一組を取り出します。</summary>
    /// <returns>指定された一つの要素です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        NodeValue collection = CollectionItemSelection.Read(inputs);
        NodeValue? index = inputs.GetValueOrDefault("index");
        if (index?.Kind != EValueKind.Json || index.Json.ValueKind != System.Text.Json.JsonValueKind.Number)
        {
            throw new FormatException("pick.index.integer");
        }
        return CollectionItemSelection.Select(collection, ScalarNumbers.Integer(ScalarNumbers.Token(index)));
    }
}
