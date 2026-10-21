using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>型と値を維持したまま、配線の意味を説明します。</summary>
public sealed class CommentNode : PureValueNode
{
    /// <summary>コメントは直接記入し、表示名は通常入力からも指定できます。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("value.comment", "Comment", "Values",
        """{"type":"object","properties":{"comment":{"type":"string","maxLength":65536,"x-multiline":true,"x-connectable":false}}}""",
        inputs: [new("value", Required: true), new("displayName", EPortValueKind.Json)]) with
    {
        Description = "Pass the value and type through unchanged. Optionally name the output and describe its meaning here.",
    };

    /// <summary>表示名とコメントをデータへ混ぜません。</summary>
    /// <returns>接続元と同一の値です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        string namePort = "displayName", valuePort = "value";
        if (inputs.TryGetValue(namePort, out NodeValue? name) && (name.Kind != EValueKind.Json || name.Json.ValueKind != System.Text.Json.JsonValueKind.String))
        {
            throw new FormatException("displayName.invalid");
        }
        if (!inputs.TryGetValue(valuePort, out NodeValue? value))
        {
            throw new FormatException("input.required");
        }
        return value;
    }
}
