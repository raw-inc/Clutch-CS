using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;

/// <summary>展開した公開境界を、通常の値依存として中継します。</summary>
internal sealed class SubworkflowPortNode : PureValueNode
{
    /// <summary>定義用の境界と同じ設定を使い、実行グラフ内では値を渡します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = new SubworkflowBoundaryNode(false).Descriptor with
    {
        Type = SubworkflowInterface.PortType, IsHidden = true, IsValueOperation = true, CanEvaluateStatically = true,
        Inputs = [new("value")], Outputs = [new("value")], IsTerminal = false,
    };

    /// <summary>未供給だけに既定値を適用し、nullと型を保持します。</summary>
    /// <returns>境界の公開型で検証した値です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        SubworkflowField field = SubworkflowInterface.ReadField(definition);
        NodeValue value = inputs.GetValueOrDefault("value") ?? field.DefaultValue ?? throw new FormatException("recipe.input.required");
        SubworkflowValues.ValidateValue(value, field.Type);
        return PathTypeMetadata.Apply(value, field.Type);
    }
}
