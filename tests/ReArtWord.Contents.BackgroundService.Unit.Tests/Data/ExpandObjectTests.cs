using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>Object展開の個別出力と、条件付きスキーマを断定しない境界を確認します。</summary>
public sealed class ExpandObjectTests
{
    /// <summary>解析結果のスキーマはスキーマ文書ではなくデータ全体に適用されます。</summary>
    /// <returns>各解析器の項目出力と制約違反を確認します。</returns>
    [Theory]
    [InlineData("json", "{\"age\":10}", "{\"age\":-1}")]
    [InlineData("yaml", "age: 10", "age: -1")]
    [InlineData("toml", "age = 10", "age = -1")]
    public async Task ParsersValidateTheCompleteParsedDataAsync(string format, string valid, string invalid)
    {
        string settings = """{"valueMode":"schema","valueSchema":{"type":"object","required":["age"],"properties":{"age":{"type":"integer","minimum":0}},"additionalProperties":false}}""";
        SerializationNode node = new(format, false);
        NodeResult accepted = await node.ExecuteAsync(NodeTestContext.Create(settings, NodeValue.FromObject(valid)));
        Assert.Equal(EOutcome.Succeeded, accepted.Outcome);
        Assert.True(PropertyPorts.TryRead(accepted.Outputs, "value/age", out NodeValue? age));
        Assert.Equal(10, age!.Json.GetInt32());
        Assert.Equal(EOutcome.Failed, (await node.ExecuteAsync(NodeTestContext.Create(settings, NodeValue.FromObject(invalid)))).Outcome);
        NodeDefinition definition = new("parse", node.Descriptor.Type, JsonSerializer.Deserialize<JsonElement>(settings));
        NodeDescriptor resolved = ConfiguredNodePorts.Resolve(node.Descriptor, definition)!;
        Assert.Contains(resolved.Outputs, port => string.Equals(port.Name, "value/age", StringComparison.Ordinal));
    }

    private const string BatchSchema = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["items"],"properties":{"items":{"type":"array","items":{"$ref":"#/$defs/item"}}},
         "$defs":{"item":{"type":"object","required":["kind","details"],"properties":{"kind":{"type":"string"},"details":{"type":"object"}},
         "allOf":[{"if":{"properties":{"kind":{"const":"message"}},"required":["kind"]},"then":{"properties":{"details":{"$ref":"#/$defs/messageDetails"}}}}]},
         "messageDetails":{"type":"object","additionalProperties":false,"required":["subject","text"],"properties":{"subject":{"type":"string","minLength":1},"text":{"type":"string","minLength":1}}}}}
        """;

    /// <summary>HTTPの型指定からForEachと展開ノードへ、条件付き項目名を引き継ぎます。</summary>
    [Fact]
    public void ConditionalDetailsExposeCandidatesWithoutInventingRequiredStrings()
    {
        NodeDefinition http = new("http", "http.listen", JsonSerializer.SerializeToElement(new { bodyMode = "schema", bodySchema = BatchSchema }));
        NodeDefinition each = new("each", "control.forEach", JsonSerializer.SerializeToElement(new { }));
        NodeDefinition expand = new("expand", "data.object.expand", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition flow = new("flow", "flow", [http, each, expand], [new("http", "body/items", "each", "value"), new("each", "item/details", "expand", "value")]);
        NodeDescriptor descriptor = new ExpandObjectNode().Descriptor;
        NodeDescriptor resolved = ConfiguredNodePorts.Resolve(descriptor, expand, flow)!;
        Assert.Contains(resolved.Outputs, port => string.Equals(port.Name, "value/subject", StringComparison.Ordinal));
        Assert.Contains(resolved.Outputs, port => string.Equals(port.Name, "value/text", StringComparison.Ordinal));
        ValueTypeDefinition type = new WorkflowTypeInference(flow, [descriptor]).PortType("expand", "value/text", true)!;
        Assert.Equal("any", type.Type);
        Assert.True(type.Optional);
        WorkflowDefinition existing = flow with { Wires = [.. flow.Wires, new("expand", "value/other", "target", "value")] };
        Assert.Contains(ConfiguredNodePorts.Resolve(descriptor, expand, existing)!.Outputs, port => string.Equals(port.Name, "value/other", StringComparison.Ordinal));
        NodeDefinition validation = expand with { Settings = JsonSerializer.SerializeToElement(new { objectMode = "schema", objectSchema = BatchSchema }) };
        Dictionary<string, NodeValue> input = new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { items = new[] { new { kind = "file", details = new { text = 42 } } } }) };
        Assert.Equal(EOutcome.Succeeded, new ExpandObjectNode().Evaluate(validation, input).Outcome);
        input["value"] = NodeValue.FromObject(new { items = new[] { new { kind = "message", details = new { text = 42 } } } });
        Assert.Equal(EOutcome.Failed, new ExpandObjectNode().Evaluate(validation, input).Outcome);
    }

    /// <summary>JSON/YAML/TOMLの解析結果から同じObject出力を配線で取り出せます。</summary>
    /// <returns>実コンパイルと実行結果です。</returns>
    [Theory]
    [InlineData("json", "{\"details\":{\"subject\":\"Status\",\"text\":\"hello\"}}")]
    [InlineData("yaml", "details:\n  subject: Status\n  text: hello")]
    [InlineData("toml", "[details]\nsubject = \"Status\"\ntext = \"hello\"")]
    public async Task ParsedObjectsFlowThroughNestedPropertyOutputsAsync(string format, string text)
    {
        WorkflowFixture fixture = new(new SerializationNode(format, false), new ExpandObjectNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition parse = new("parse", "data." + format + ".parse", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(text) });
        JsonElement manual = JsonSerializer.Deserialize<JsonElement>("""{"objectMode":"manual","objectType":{"type":"object","properties":{"details":{"type":"object","properties":{"subject":{"type":"string"},"text":{"type":"string"}}}}}}""");
        NodeDefinition first = new("first", "data.object.expand", manual);
        NodeDefinition second = new("second", "data.object.expand", JsonSerializer.SerializeToElement(new { }));
        NodeDefinition output = new("output", "control.pass", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition flow = new("flow", "flow", [parse, first, second, output], [new("parse", "value", "first", "value"), new("first", "value/details", "second", "value"), new("second", "value/text", "output", "value")]);
        CompilationResult compiled = fixture.Compiler.Compile(flow);
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal("hello", Assert.Single(Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "output", StringComparison.Ordinal)).Targets).Result.Outputs["value"].GetString());
    }

    /// <summary>手動・スキーマの型制約とObject以外の入力を実行時にも検証します。</summary>
    /// <returns>暗黙変換せず失敗する境界です。</returns>
    [Theory]
    [InlineData("auto", "[]", false)]
    [InlineData("auto", "\"text\"", false)]
    [InlineData("auto", "null", false)]
    [InlineData("auto", "{}", true)]
    [InlineData("manual", "{\"text\":42}", false)]
    [InlineData("manual", "{\"text\":\"ok\"}", true)]
    [InlineData("schema", "{\"text\":\"\"}", false)]
    [InlineData("schema", "{\"text\":\"ok\"}", true)]
    public async Task ExplicitTypesAreValidatedAsync(string mode, string json, bool success)
    {
        string[] required = ["text"];
        string settings = JsonSerializer.Serialize(new { objectMode = mode, objectType = new { type = "object", properties = new { text = new { type = "string" } } }, objectSchema = new { type = "object", required, properties = new { text = new { type = "string", minLength = 1 } } } });
        NodeResult result = await new ExpandObjectNode().ExecuteAsync(NodeTestContext.Create(settings, NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json))));
        Assert.Equal(success, result.Outcome == EOutcome.Succeeded);
        if (!success)
        {
            Assert.Empty(result.Outputs);
        }
    }

    /// <summary>任意キーを変更せず抽出し、入力元の編集はプレビューへ反映します。</summary>
    [Fact]
    public void EscapedNamesAndPreviewUseExistingPropertySemantics()
    {
        NodeValue value = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("""{"a/b~c":9007199254740993,"text":null}"""));
        NodeDefinition node = new("expand", "data.object.expand", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = value });
        WorkflowDefinition flow = new("flow", "flow", [node], []);
        Assert.Equal(value, new WorkflowSettingValues(flow).Output(node.Id));
        NodeResult result = new ExpandObjectNode().Evaluate(node, node.Inputs!);
        Assert.True(PropertyPorts.TryRead(result.Outputs, "value/a~1b~0c", out NodeValue? number));
        Assert.Equal("9007199254740993", number!.Json.GetRawText());
        Assert.True(PropertyPorts.TryRead(result.Outputs, "value/missing", out NodeValue? missing));
        Assert.Equal(JsonValueKind.Null, missing!.Json.ValueKind);
        Assert.False(PropertyPorts.TryRead(result.Outputs, "value/missing", out _, true));
    }
}
