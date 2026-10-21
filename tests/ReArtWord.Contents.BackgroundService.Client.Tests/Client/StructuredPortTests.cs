using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>個別項目出力の推測・表示・接続を共通契約として検証します。</summary>
public sealed class StructuredPortTests
{
    /// <summary>任意名の項目を表示し、元のitem出力も保持します。</summary>
    [Theory]
    [InlineData("control.forEach")]
    [InlineData("control.parallelForEach")]
    public void AutoInferenceIncludesArbitraryAndOptionalProperties(string nodeType)
    {
        NodeDefinition node = new("each", nodeType, JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new object[] { new { age = 10, name = "a" }, new { age = 20 } }) });
        WorkflowDefinition document = new("flow", "flow", [node], []);
        NodeDescriptor descriptor = Descriptor(nodeType, "item");
        NodeDescriptor resolved = EditorNodeDescriptor.Resolve(descriptor, node, document)!;
        Assert.Contains(resolved.Outputs, port => string.Equals(port.Name, "item", StringComparison.Ordinal));
        Assert.Contains(resolved.Outputs, port => string.Equals(port.Name, "item/age", StringComparison.Ordinal));
        Assert.Contains(resolved.Outputs, port => string.Equals(port.Name, "item/name", StringComparison.Ordinal));
        WorkflowTypeInference inference = new(document, [descriptor]);
        Assert.Equal("integer", inference.PortType("each", "item/age", true)!.Type);
        Assert.True(inference.PortType("each", "item/name", true)!.Optional);
    }

    /// <summary>手動・スキーマ方式は入力がまだ不明でも項目を宣言できます。</summary>
    [Theory]
    [InlineData("manual")]
    [InlineData("schema")]
    public void DeclaredTypesCreatePortsBeforeRuntimeInputExists(string mode)
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new
        {
            elementMode = mode,
            elementType = new { type = "object", properties = new Dictionary<string, object>(StringComparer.Ordinal) { ["item-id"] = new { type = "integer" } } },
            elementSchema = new { type = "object", properties = new Dictionary<string, object>(StringComparer.Ordinal) { ["item-id"] = new { type = "integer" } } },
        });
        NodeDefinition node = new("each", "control.forEach", settings);
        WorkflowDefinition document = new("flow", "flow", [node], []);
        Assert.Contains(EditorNodeDescriptor.Resolve(Descriptor(node.Type, "item"), node, document)!.Outputs,
            port => string.Equals(port.Name, "item/item-id", StringComparison.Ordinal));
    }

    /// <summary>同じ項目出力を通常の値ノードにも展開し、接続値プレビューでも使います。</summary>
    [Fact]
    public void ConstantObjectFieldsAreTypedAndPreviewable()
    {
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { type = "auto", text = "{\"item-id\":1,\"a/b\":\"text\"}" }));
        NodeDefinition target = new("log", "control.log", JsonSerializer.SerializeToElement(new { }));
        string port = PropertyPorts.Name("value", "a/b");
        WorkflowDefinition document = new("flow", "flow", [source, target], [new("source", port, "log", "message")]);
        NodeDescriptor[] catalog = [Descriptor("value.constant", "value"), Descriptor("control.log", "value") with { Inputs = [new("message", EPortValueKind.Json)] }];
        Assert.Contains(EditorNodeDescriptor.Resolve(catalog[0], source, document)!.Outputs, item => string.Equals(item.Name, port, StringComparison.Ordinal));
        Assert.Equal("text", new WorkflowSettingValues(document).Input(target, "message")!.GetString());
        Assert.True(GraphConnectionPolicy.CanConnect(document, catalog, document.Wires[0]));
    }

    /// <summary>配列作成でも明示した要素型を接続判定へ反映します。</summary>
    [Theory]
    [InlineData("manual")]
    [InlineData("schema")]
    public void ArrayElementSettingsRejectKnownWrongConnections(string mode)
    {
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { type = "string", text = "wrong" }));
        NodeDefinition target = new("array", "value.array", JsonSerializer.SerializeToElement(new { elementMode = mode, elementType = new { type = "integer" }, elementSchema = new { type = "integer" } }));
        WorkflowDefinition document = new("flow", "flow", [source, target], [new("source", "value", "array", "item")]);
        NodeDescriptor[] catalog = [Descriptor(source.Type, "value"), Descriptor(target.Type, "value") with { Inputs = [new("item", EPortValueKind.Json)] }];
        Assert.False(GraphConnectionPolicy.CanConnect(document, catalog, document.Wires[0]));
    }

    /// <summary>型定義へ渡すJSON Schemaは元の制約を保持し、ヒントだけを信用しません。</summary>
    [Fact]
    public void SchemaHintsRetainConstraintsAndLocalDefinitions()
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"$defs":{"person":{"type":"object","properties":{"age":{"type":"integer","minimum":0}},"required":["age"]}},"$ref":"#/$defs/person"}""");
        ValueTypeDefinition type = JsonSchemaTypes.Read(schema);
        Assert.Equal("object", type.Type);
        Assert.False(type.Properties!["age"].Optional);
        Assert.Equal(schema.GetRawText(), type.Schema!.Value.GetRawText());
        NodeValue misleading = NodeValue.FromObject(new { type = "string", schema });
        Assert.Equal("object", ValueTypeInference.Descriptor(misleading)!.Type);
    }

    /// <summary>実行側でしか制約検証できない定数を、確定済みプレビューとしません。</summary>
    [Fact]
    public void SchemaConstrainedConstantsDoNotPublishUnvalidatedPreviews()
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"string","minLength":5}""");
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { text = "a" }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["type"] = NodeValue.FromObject(new { type = "string", schema }) });
        NodeDefinition target = new("target", "control.pass", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition document = new("flow", "flow", [source, target], [new("source", "value", "target", "value")]);
        Assert.Null(new WorkflowSettingValues(document).Input(target, "value"));
    }

    /// <summary>必須設定に対して存在しない項目を明示nullと取り違えません。</summary>
    [Fact]
    public void StrictPropertyReadsDistinguishMissingFromNull()
    {
        Dictionary<string, NodeValue> values = new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { present = (object?)null }) };
        Assert.True(PropertyPorts.TryRead(values, "value/present", out NodeValue? present, true));
        Assert.Equal(JsonValueKind.Null, present!.Json.ValueKind);
        Assert.False(PropertyPorts.TryRead(values, "value/missing", out _, true));
        Assert.True(PropertyPorts.TryRead(values, "value/missing", out NodeValue? missing));
        Assert.Equal(JsonValueKind.Null, missing!.Json.ValueKind);
    }

    /// <summary>型の構築を必要としない最小カタログです。</summary>
    /// <returns>対象の主出力です。</returns>
    private static NodeDescriptor Descriptor(string type, string output) => new(type, type, "Data", "", ENodeKind.Action,
        EExecutionLocation.Independent, [new("value", EPortValueKind.Json)], [new(output, EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
