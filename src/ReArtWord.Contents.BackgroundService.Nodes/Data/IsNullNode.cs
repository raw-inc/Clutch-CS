using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>実際に届いたnullだけをtrueとし、空の値や未到着と区別します。</summary>
public sealed class IsNullNode : PureValueNode
{
    /// <summary>任意の入力値を受け取り、真偽値を返します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.isNull", "IsNull", "Data", """{"type":"object","properties":{}}""",
        inputs: [new("value", Required: true)], outputs: [new("value", EPortValueKind.Json) { DataType = new("boolean") }]) with
    {
        Description = "Return true only for null. Empty text, false, zero, arrays, dictionaries and binary data return false. Missing or failed input is not treated as null."
    };

    /// <summary>Null → Defaultと同じnull判定を使い、未供給の入力には結果を作りません。</summary>
    /// <returns>入力値が明示的なnullかを表す真偽値です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
        => NodeValue.FromObject(DefaultIfNullValues.IsNull(inputs.GetValueOrDefault("value") ?? throw new FormatException("input.required")));
}
