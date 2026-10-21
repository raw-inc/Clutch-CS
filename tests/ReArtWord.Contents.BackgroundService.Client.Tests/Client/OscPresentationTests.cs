using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>OSCの固定引数が表示と接続検証で同じ意味になることを確認します。</summary>
public sealed class OscPresentationTests
{
    /// <summary>複数引数の型と色を、内部のパケット形式とは分けて表示します。</summary>
    [Theory]
    [InlineData("i", "integer")]
    [InlineData("h", "int64")]
    public void ArgumentPortsHaveDistinctTypesAndColors(string tag, string integerType)
    {
        NodeDefinition node = new("receive", "osc.udp.receive", JsonSerializer.SerializeToElement(new { argumentTypes = new[] { tag, "s", "b", "boolean" } }));
        NodeDescriptor descriptor = Descriptor(node.Type, ENodeKind.Source);
        NodeDescriptor resolved = OscPorts.Resolve(descriptor, node)!;
        Assert.Equal(["value", "address", "arguments", "argument1", "argument2", "argument3", "argument4"], resolved.Outputs.Select(port => port.Name));
        GraphTypeInference inference = new(new("osc", "OSC", [node], []), [descriptor]);
        Assert.Equal("integer", inference.Port(node.Id, "argument1", true).Key);
        Assert.Equal(integerType, inference.Port(node.Id, "argument1", true).InferredType);
        Assert.Equal("string", inference.Port(node.Id, "argument2", true).Key);
        Assert.Equal("binary", inference.Port(node.Id, "argument3", true).Key);
        Assert.Equal("boolean", inference.Port(node.Id, "argument4", true).Key);
        Assert.Equal("string", inference.Port(node.Id, "address", true).Key);
        NodeDescriptor configured = ConfiguredNodePorts.Resolve(descriptor, node)!;
        Assert.Single(configured.Outputs, port => port.Name is "start");
        Assert.Equal("trigger", inference.Port(node.Id, "start", true).Key);
        Assert.Equal("port.description.sourceStart", inference.Port(node.Id, "start", true).Description);
    }

    /// <summary>固定引数への既知の型違いを、配線時点で拒否します。</summary>
    [Fact]
    public void TypedInputRejectsTextForAnIntegerArgument()
    {
        NodeDefinition receive = new("receive", "osc.udp.receive", JsonSerializer.Deserialize<JsonElement>("""{"argumentTypes":["s"]}"""));
        NodeDefinition send = new("send", "osc.udp.send", JsonSerializer.Deserialize<JsonElement>("""{"argumentTypes":["i"]}"""));
        WorkflowDefinition workflow = new("osc", "OSC", [receive, send], []);
        NodeDescriptor[] catalog = [Descriptor(receive.Type, ENodeKind.Source), Descriptor(send.Type, ENodeKind.Action)];
        Assert.False(WorkflowConnectionTypes.CanConnect(workflow, catalog, new(receive.Id, "argument1", send.Id, "argument1")));
        send = send with { Settings = receive.Settings };
        Assert.True(WorkflowConnectionTypes.CanConnect(workflow with { Nodes = [receive, send] }, catalog, new(receive.Id, "argument1", send.Id, "argument1")));
    }

    /// <summary>通信ノードの固定部分だけを定義します。</summary>
    /// <returns>設定前のカタログです。</returns>
    private static NodeDescriptor Descriptor(string type, ENodeKind kind) => new(type, type, "Communication", "", kind, EExecutionLocation.ExplicitPeer,
        [new("value", Required: true), new("address")], [new("value"), new("address"), new("arguments")], ["success"], JsonSerializer.Deserialize<JsonElement>("{}"));
}
