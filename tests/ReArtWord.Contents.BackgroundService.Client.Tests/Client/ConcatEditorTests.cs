using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>接続・直接入力で増える口と、既知型の接続境界を検証します。</summary>
public sealed class ConcatEditorTests
{
    /// <summary>二つが埋まると三つになり、未接続口は番号を詰めません。</summary>
    [Fact]
    public void PortsExpandFromConnectionsAndConstantsWithoutRenumbering()
    {
        NodeDefinition node = new("concat", ConcatPorts.NodeType, JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition document = new("flow", "flow", [node], []);
        Assert.Equal(3, EditorNodeDescriptor.Resolve(Descriptor(), node, document)!.Inputs.Count);
        document = document with { Wires = [new("a", "value", "concat", "input2"), new("b", "value", "concat", "input1")] };
        Assert.Equal(4, EditorNodeDescriptor.Resolve(Descriptor(), node, document)!.Inputs.Count);
        node = node with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input3"] = NodeValue.FromObject("C") } };
        document = document with { Nodes = [node] };
        Assert.Equal(5, EditorNodeDescriptor.Resolve(Descriptor(), node, document)!.Inputs.Count);
        document = document with { Wires = [document.Wires[1]] };
        Assert.Equal(["separator", "input1", "input2", "input3"], EditorNodeDescriptor.Resolve(Descriptor(), node, document)!.Inputs.Select(port => port.Name));
        Assert.Equal("C", node.Inputs!["input3"].GetString());
        Assert.Equal(64, ConcatPorts.VisibleCount(Enumerable.Range(1, 64).Select(ConcatPorts.Name)));
        Assert.Equal(0, ConcatPorts.Index("input01"));
        Assert.Equal(0, ConcatPorts.Index("input65"));
    }

    /// <summary>別の入力の既知型と一致しない接続だけを拒否します。</summary>
    [Theory]
    [InlineData("\"a\"", "\"b\"", true, "string")]
    [InlineData("[1]", "[2]", true, "array")]
    [InlineData("[]", "[2]", true, "array")]
    [InlineData("[1]", "[\"b\"]", false, "array")]
    [InlineData("\"a\"", "[2]", false, "string")]
    [InlineData("\"a\"", "2", false, "string")]
    public void ConnectionsCheckOtherInputs(string first, string second, bool allowed, string output)
    {
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { type = "auto", text = second }));
        NodeDefinition target = new("concat", ConcatPorts.NodeType, JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input1"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(first)) });
        WorkflowDefinition document = new("flow", "flow", [source, target], []);
        NodeDescriptor[] catalog = [Descriptor(), new("value.constant", "Value", "Values", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }))];
        WireDefinition wire = new("source", "value", "concat", "input2");
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, catalog, wire));
        if (allowed)
        {
            WorkflowDefinition connected = document with { Wires = [wire] };
            Assert.Equal(output, new WorkflowTypeInference(connected, catalog).PortType("concat", "value", true)!.Type);
            Assert.NotNull(new WorkflowSettingValues(connected).Output("concat"));
        }
    }

    /// <summary>区切りは文字列だけを受け、可変入力の使用数には含めません。</summary>
    [Theory]
    [InlineData("string", true)]
    [InlineData("integer", false)]
    [InlineData("boolean", false)]
    public void SeparatorHasItsOwnInputContract(string type, bool allowed)
    {
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { type, text = "1" }));
        NodeDefinition target = new("concat", ConcatPorts.NodeType, JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition document = new("flow", "flow", [source, target], [new("source", "value", "concat", "separator")]);
        NodeDescriptor[] catalog = [Descriptor(), new("value.constant", "Value", "Values", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }))];
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, catalog, document.Wires[0]));
        Assert.Equal(["separator", "input1", "input2"], EditorNodeDescriptor.Resolve(Descriptor(), target, document)!.Inputs.Select(port => port.Name));
    }

    /// <summary>実行時の全入力を宣言し、表示側だけ必要な口へ絞ります。</summary>
    /// <returns>結合の公開契約です。</returns>
    private static NodeDescriptor Descriptor() => new(ConcatPorts.NodeType, "Concat", "Data", "", ENodeKind.Action, EExecutionLocation.Independent,
        [new("separator", EPortValueKind.Json) { DataType = new("string", Optional: true) },
            .. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ConcatPorts.Name(index), EPortValueKind.Json))],
        [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
