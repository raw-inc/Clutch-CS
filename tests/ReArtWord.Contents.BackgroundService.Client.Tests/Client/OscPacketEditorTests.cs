using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>OSC組み立ての可変入力と、既存ノードへの配線を確認します。</summary>
public sealed class OscPacketEditorTests
{
    /// <summary>引数だけが増え、OSCアドレスと引数番号を維持します。</summary>
    [Fact]
    public void PacketArgumentPortsGrowWithoutRenumbering()
    {
        NodeDefinition node = new("packet", OscPorts.PacketNodeType, JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition document = new("flow", "flow", [node], []);
        Assert.Equal(["address", "packet", "input1", "input2"], EditorNodeDescriptor.Resolve(PacketDescriptor(), node, document)!.Inputs.Select(port => port.Name));
        document = document with { Wires = [new("a", "value", "packet", "input2"), new("b", "value", "packet", "input1")] };
        Assert.Equal(["address", "packet", "input1", "input2", "input3"], EditorNodeDescriptor.Resolve(PacketDescriptor(), node, document)!.Inputs.Select(port => port.Name));
        node = node with { Inputs = new Dictionary<string, NodeValue> { ["input10"] = NodeValue.Null } };
        document = document with { Nodes = [node], Wires = [] };
        Assert.Equal(12, EditorNodeDescriptor.Resolve(PacketDescriptor(), node, document)!.Inputs.Count);
    }

    /// <summary>文字列・Path・配列・Binaryを入力でき、既存のOSC送信へObjectとして接続できます。</summary>
    [Fact]
    public void PacketConnectsToSendersAndAcceptsConvertibleInputs()
    {
        NodeDefinition packet = new("packet", OscPorts.PacketNodeType, JsonSerializer.SerializeToElement(new { }));
        NodeDefinition source = new("source", "test.source", packet.Settings);
        NodeDefinition send = new("send", "osc.udp.send", packet.Settings);
        WorkflowDefinition workflow = new("flow", "flow", [packet, source, send], []);
        NodeDescriptor[] catalog = [PacketDescriptor(), new(source.Type, "Source", "Values", "", ENodeKind.Action, EExecutionLocation.Independent,
            [], [new("packet") { DataType = new("object") }, new("text") { DataType = new("string") }, new("path") { DataType = new("path") }, new("array") { DataType = new("array") }, new("bytes", EPortValueKind.Binary)], ["success"], packet.Settings),
            new(send.Type, "Send", "Communication", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [new("value", EPortValueKind.Json, true)], [], ["success"], packet.Settings)];
        Assert.True(GraphConnectionPolicy.CanConnect(workflow, catalog, new("source", "packet", "packet", "packet")));
        Assert.False(GraphConnectionPolicy.CanConnect(workflow, catalog, new("source", "text", "packet", "packet")));
        Assert.True(GraphConnectionPolicy.CanConnect(workflow, catalog, new("source", "text", "packet", "address")));
        Assert.True(GraphConnectionPolicy.CanConnect(workflow, catalog, new("source", "path", "packet", "address")));
        Assert.True(GraphConnectionPolicy.CanConnect(workflow, catalog, new("source", "array", "packet", "input1")));
        Assert.True(GraphConnectionPolicy.CanConnect(workflow, catalog, new("source", "bytes", "packet", "input2")));
        Assert.False(GraphConnectionPolicy.CanConnect(workflow, catalog, new("source", "bytes", "packet", "address")));
        Assert.True(GraphConnectionPolicy.CanConnect(workflow, catalog, new("packet", "value", "send", "value")));
        Assert.Equal("object", new GraphTypeInference(workflow, catalog).Port("packet", "value", true).Key);
        Assert.DoesNotContain(ConfiguredNodePorts.Resolve(PacketDescriptor(), packet)!.Outputs, port => port.Name is "$completed" or "$succeeded");
    }

    /// <summary>編集に届く純粋ノードの公開契約です。</summary>
    /// <returns>全引数を保持したカタログ項目です。</returns>
    private static NodeDescriptor PacketDescriptor() => new(OscPorts.PacketNodeType, "Build OSC packet", "Communication", "", ENodeKind.Action, EExecutionLocation.Independent,
        [new("address", EPortValueKind.Json), new("packet", EPortValueKind.Json), .. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ConcatPorts.Name(index)))],
        [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
