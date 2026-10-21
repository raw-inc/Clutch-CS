using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>使わない入力を新規候補から除き、既存の依存関係は保持します。</summary>
public sealed class NodeUsabilityTests
{
    /// <summary>旧入力は接続済みの場合だけ残し、定義の自動書換えは行いません。</summary>
    [Fact]
    public void LegacyInputsAreHiddenForNewNodesAndRetainedForExistingWires()
    {
        NodeDefinition node = new("n", "process.start", JsonSerializer.Deserialize<JsonElement>("{}"));
        NodeDescriptor descriptor = new(node.Type, node.Type, "Processes", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer,
            [new("value") { IsLegacy = true }], [new("value")], ["success"], JsonSerializer.Deserialize<JsonElement>("{}"));
        WorkflowDefinition document = new("w", "w", [node], []);
        Assert.Empty(EditorNodeDescriptor.Resolve(descriptor, node, document)!.Inputs);
        WireDefinition wire = new("source", "value", node.Id, "value");
        Assert.Single(EditorNodeDescriptor.Resolve(descriptor, node, document with { Wires = [wire] })!.Inputs);
        Assert.Single(descriptor.Inputs);
        Assert.Equal("unused", new GraphTypeInference(document, [descriptor]).Port(node.Id, "value", false).Key);
        Assert.Empty(document.Wires);
    }

    /// <summary>形式を変えたときに使える候補だけを返し、保存値を黙って変更しません。</summary>
    [Fact]
    public void DependentChoicesDoNotMutateSavedSelections()
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"string","enum":["a","b"],"x-choicesWhen":{"property":"format","options":{"first":["a"],"second":["b"]}}}""");
        JsonElement settings = JsonSerializer.Deserialize<JsonElement>("""{"format":"first","codec":"b"}""");
        JsonElement selected = NodeSettingVisibility.Choices(schema, settings);
        Assert.Equal("a", Assert.Single(selected.GetProperty("enum").EnumerateArray()).GetString());
        Assert.Equal("b", settings.GetProperty("codec").GetString());
        Assert.Equal(2, schema.GetProperty("enum").GetArrayLength());
    }
}
