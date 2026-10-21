using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>クラスタ内の選択したピアIDを、転送元などの設定へ渡します。</summary>
public sealed class PeerValueNode : PureValueNode
{
    /// <summary>実行PC選択と区別した、単一ピアの参照です。</summary>
    public override NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("value.peer", "Peer selection", "Values",
        """{"type":"object","required":["peerId"],"properties":{"peerId":{"type":"string","minLength":1,"description":"Select a peer in this cluster. Connect the output to Source peer for a peer file transfer."}}}""",
        inputs: [], outputs: [new("value", EPortValueKind.Json)]) with
    {
        Description = "Select one peer and reuse its ID, for example as the source of a file transfer. The transfer node runs on the destination PC.",
    };

    /// <summary>選択した識別子をそのまま固定値として返します。</summary>
    /// <returns>ピア識別子です。</returns>
    protected override NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
        => NodeValue.FromObject(new NodeSettings(definition.Settings).ReadText("peerId")!);
}
