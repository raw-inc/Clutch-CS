using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>NIC識別子を値として保持し、実在確認や通信は行いません。</summary>
public sealed class InterfaceValueNode : PureValueNode
{
    /// <summary>OS固有の識別子をGUIDに限定せず保持します。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("value.interface", "Network interface", "Values",
        """{"type":"object","required":["interfaceId"],"properties":{"interfaceId":{"type":"string","minLength":1}}}""",
        inputs: [], outputs: [new("value", EPortValueKind.Json)]) with { Description = "Choose a network interface identifier. The consuming node validates it on its execution peer." };

    /// <summary>元の識別子文字列をそのまま返します。</summary>
    /// <returns>JSON文字列です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs) => NodeValue.FromObject(new NodeSettings(definition.Settings).ReadText("interfaceId"));
}
