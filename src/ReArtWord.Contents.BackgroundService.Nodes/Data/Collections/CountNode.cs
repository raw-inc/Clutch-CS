using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;

/// <summary>配列または辞書の直下にある項目数を、nullや入れ子を省略せず数えます。</summary>
public sealed class CountNode : PureValueNode
{
    /// <summary>任意の要素型の配列または辞書を受け取り、整数を返します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.count", "Count collection items", "Data", """{"type":"object","properties":{}}""",
        inputs: [new("value", EPortValueKind.Json, true)],
        outputs: [new("value", EPortValueKind.Json) { DataType = new("integer") }]) with
        {
            IsValueOperation = true,
            Description = "Count the immediate items in an array or dictionary. Empty collections return zero; null and nested values each count as one item. Other inputs fail."
        };

    /// <summary>配列または辞書を展開せず、直下の項目数を取得します。</summary>
    /// <returns>0以上の整数です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        NodeValue? value = inputs.GetValueOrDefault("value");
        if (value?.Kind != EValueKind.Json || value.Json.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            throw new FormatException("count.input.collection");
        }
        return NodeValue.FromObject(CollectionItemSelection.Count(value));
    }
}
