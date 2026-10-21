using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Targets;

/// <summary>比較条件を保存し、一件の開始時に低負荷PCを選べるようにします。</summary>
public sealed class LowestResourcePcNode : PureValueNode
{
    /// <summary>比較対象のリソース、単位、GPUと候補入力を公開します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(PcSelectionPorts.LowestNode, "Select least busy PC", "Execution",
        """{"type":"object","required":["resource"],"properties":{"resource":{"type":"string","enum":["cpu","ram","gpu","vram"]},"measurement":{"type":"string","enum":["percent","bytes"],"x-choicesWhen":{"property":"resource","options":{"cpu":["percent"],"gpu":["percent"],"ram":["percent","bytes"],"vram":["percent","bytes"]}}},"gpuId":{"type":"string","minLength":1,"x-visibleWhen":{"property":"resource","values":["gpu","vram"]}}}}""",
        inputs: [new(PcSelectionPorts.Candidates, EPortValueKind.PcSelection)], outputs: [new("value", EPortValueKind.PcSelection)]) with
    {
        Description = "Choose the PC with the lowest observed resource use. Measurements and the selected PC are fixed when an execution begins.",
    };

    /// <summary>実測値を設定へ保存せず、比較の条件だけを構築します。</summary>
    /// <returns>必要な観測を一件の実行計画で解決する選択条件。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        NodeSettings settings = new(definition.Settings);
        EPcResource resource = Enum.Parse<EPcResource>(settings.ReadText("resource")!, true);
        string? gpu = null;
        if (resource is EPcResource.Gpu or EPcResource.Vram)
        {
            gpu = settings.ReadText("gpuId");
        }
        PcResourceChoice choice = new(resource, string.Equals(settings.ReadText("measurement"), "bytes", StringComparison.Ordinal), gpu);
        IReadOnlyList<PcSelection>? within = null;
        if (inputs.TryGetValue(PcSelectionPorts.Candidates, out NodeValue? candidates))
        {
            within = PcSelectionValue.Read(candidates);
        }
        NodeValue result = NodeValue.FromObject(new[] { new PcSelection(Lowest: choice, Within: within) });
        _ = PcSelectionValue.Read(result);
        return result;
    }
}
