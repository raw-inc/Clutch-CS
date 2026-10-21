using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>展開ノードの自動・明示出力と、既知の不正な配線を確認します。</summary>
public sealed class ExpandObjectEditorTests
{
    /// <summary>自動受信の本文は任意型、JSONの項目は条件付き候補として区別します。</summary>
    [Theory]
    [InlineData("json", "object", true)]
    [InlineData("auto", "any", true)]
    [InlineData("text", "string", false)]
    [InlineData("binary", "binary", false)]
    public void HttpBodyFormatControlsSchemaPorts(string format, string expected, bool properties)
    {
        NodeDefinition node = new("http", "http.listen", JsonSerializer.SerializeToElement(new { bodyFormat = format, bodyMode = "schema", bodySchema = new { type = "object", properties = new { age = new { type = "integer" } } } }));
        WorkflowDefinition flow = new("flow", "flow", [node], []);
        NodeDescriptor descriptor = Descriptor(node.Type) with { Outputs = [new("body")] };
        WorkflowTypeInference inference = new(flow, [descriptor]);
        Assert.Equal(expected, inference.PortType(node.Id, "body", true)!.Type);
        NodeDescriptor resolved = EditorNodeDescriptor.Resolve(descriptor, node, flow)!;
        Assert.Equal(properties, resolved.Outputs.Any(port => string.Equals(port.Name, "body/age", StringComparison.Ordinal)));
        if (properties)
        {
            Assert.Equal("integer", inference.PortType(node.Id, "body/age", true)!.Type);
            Assert.True(inference.PortType(node.Id, "body/age", true)!.Optional);
        }
    }

    /// <summary>開始信号はデータの真偽値表示と区別し、不正な条件の配線を拒否します。</summary>
    [Theory]
    [InlineData("control.ifStart")]
    [InlineData("control.ifNotStart")]
    public void StartSignalHasControlPresentationAndBooleanCondition(string nodeType)
    {
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { type = "integer", text = "1" }));
        NodeDefinition gate = new("gate", nodeType, JsonSerializer.SerializeToElement(new { }));
        NodeDefinition target = new("target", "control.pass", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition flow = new("flow", "flow", [source, gate, target], []);
        NodeDescriptor descriptor = Descriptor(nodeType) with { Inputs = [new("condition", EPortValueKind.Json)], Outputs = [new("start", EPortValueKind.Json)], CanEvaluateStatically = false };
        NodeDescriptor[] catalog = [Descriptor(source.Type), descriptor, Descriptor(target.Type)];
        Assert.Equal("trigger", new GraphTypeInference(flow, catalog).Port(gate.Id, "start", true).Key);
        Assert.False(GraphConnectionPolicy.CanConnect(flow, catalog, new("source", "value", "gate", "condition")));
        Assert.True(GraphConnectionPolicy.CanConnect(flow, catalog, new("gate", "start", "target", "trigger")));
    }

    /// <summary>接続元のObjectを一階層ずつ展開し、値と表示型が一致します。</summary>
    [Fact]
    public void NestedOutputsAndPreviewFollowTheInput()
    {
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { type = "auto", text = "{\"details\":{\"age\":10,\"item-id\":1,\"a/b~c\":\"ok\"}}" }));
        NodeDefinition first = new("first", "data.object.expand", JsonSerializer.SerializeToElement(new { }));
        NodeDefinition second = first with { Id = "second" };
        NodeDefinition target = new("target", "control.log", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition document = new("flow", "flow", [source, first, second, target], [new("source", "value", "first", "value"), new("first", "value/details", "second", "value"), new("second", "value/a~1b~0c", "target", "message")]);
        NodeDescriptor[] catalog = [Descriptor(source.Type), Descriptor(first.Type), Descriptor(target.Type) with { Inputs = [new("message", EPortValueKind.Json)] }];
        string[] ports = EditorNodeDescriptor.Resolve(catalog[1], second, document)!.Outputs.Select(port => port.Name).ToArray();
        Assert.Contains("value/age", ports);
        Assert.Contains("value/item-id", ports);
        Assert.Contains("value/a~1b~0c", ports);
        Assert.Equal("integer", new WorkflowTypeInference(document, catalog).PortType("second", "value/age", true)!.Type);
        Assert.True(GraphConnectionPolicy.CanConnect(document, catalog, document.Wires[2]));
        Assert.Equal("ok", new WorkflowSettingValues(document).Input(target, "message")!.GetString());
        Assert.False(GraphConnectionPolicy.CanConnect(document, catalog, new("second", "value/age", "first", "value")));
    }

    /// <summary>実行時まで未知の入力にも明示型から個別出力を準備できます。</summary>
    [Theory]
    [InlineData("manual")]
    [InlineData("schema")]
    public void ExplicitTypesExposePortsWithoutInput(string mode)
    {
        NodeDefinition node = new("expand", "data.object.expand", JsonSerializer.SerializeToElement(new
        {
            objectMode = mode,
            objectType = new { type = "object", properties = new { subject = new { type = "string" }, text = new { type = "string" } } },
            objectSchema = new { type = "object", properties = new { subject = new { type = "string" }, text = new { type = "string" } } }
        }));
        WorkflowDefinition document = new("flow", "flow", [node], []);
        Assert.Contains(EditorNodeDescriptor.Resolve(Descriptor(node.Type), node, document)!.Outputs, port => string.Equals(port.Name, "value/text", StringComparison.Ordinal));
        Assert.Equal("string", new WorkflowTypeInference(document, [Descriptor(node.Type)]).PortType(node.Id, "value/text", true)!.Type);
        Assert.Null(new WorkflowSettingValues(document).Output(node.Id));
    }

    /// <summary>出力数を制限し、自己参照schemaで無限走査しません。</summary>
    [Fact]
    public void RecursiveSchemasAndManyPropertiesStayBounded()
    {
        JsonElement recursive = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"next":{"$ref":"#"},"text":{"type":"string"}},"allOf":[{"$ref":"#"}]}""");
        Assert.Equal("object", JsonSchemaTypes.Read(recursive).Type);
        NodeDefinition node = new("expand", "data.object.expand", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(Enumerable.Range(0, 200).ToDictionary(index => "key" + index, index => index, StringComparer.Ordinal)) });
        NodeDescriptor descriptor = EditorNodeDescriptor.Resolve(Descriptor(node.Type), node, new("flow", "flow", [node], []))!;
        Assert.InRange(descriptor.Outputs.Count, 1, 129);
    }

    /// <summary>実行先を持たない値ノードとして描画します。</summary>
    /// <returns>共通プロパティ解決へ渡す最小契約です。</returns>
    private static NodeDescriptor Descriptor(string type) => new(type, type, "Data", "", ENodeKind.Action, EExecutionLocation.Independent,
        [new("value", EPortValueKind.Json)], [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }), true, true);
}
