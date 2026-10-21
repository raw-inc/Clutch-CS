using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>入力値がnullの場合だけ既定値を選び、その他の値は型を保って返します。</summary>
public sealed class DefaultIfNullNode : PureValueNode
{
    /// <summary>任意の入力値と既定値を受け、未指定の既定値はnullです。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.defaultIfNull", "Null → Default", "Data", """{"type":"object","properties":{}}""",
        inputs: [new("value", Required: true), new("defaultValue")], outputs: [new("value")]) with
    {
        Description = "Use the default only when the input is null. Preserve false, zero, empty text, arrays, dictionaries, binary data and type metadata. An omitted default returns null."
    };

    /// <summary>未供給とJSON nullを区別し、選択値の所有権を分離します。</summary>
    /// <returns>入力値、またはnull時の既定値です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
        => DefaultIfNullValues.Select(inputs.GetValueOrDefault("value") ?? throw new FormatException("input.required"), inputs.GetValueOrDefault("defaultValue"));
}
