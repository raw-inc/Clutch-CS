using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>宣言・ANY伝播・設定の静的境界を同じ接続判定で検証します。</summary>
public sealed class ConnectionContractTests
{
    /// <summary>実行時OSC値はJSONとしてログ本文へ接続でき、設定側の文字列契約は維持します。</summary>
    [Theory]
    [InlineData("i", true)]
    [InlineData("h", true)]
    [InlineData("f", true)]
    [InlineData("d", true)]
    [InlineData("s", true)]
    [InlineData("boolean", true)]
    [InlineData("b", false)]
    public void LogMessageAcceptsRuntimeJsonValues(string tag, bool allowed)
    {
        NodeDefinition source = Node("a", "osc.udp.receive") with { Settings = JsonSerializer.SerializeToElement(new { argumentTypes = new[] { tag } }) };
        NodeDefinition target = Node("b", "control.log");
        WorkflowDefinition document = new("log", "log", [source, target], []);
        NodeDescriptor log = Descriptor("control.log", EPortValueKind.Json) with
        {
            Inputs = [new("message", EPortValueKind.Json, OverridesSetting: "message")],
            SettingsSchema = JsonSerializer.Deserialize<JsonElement>("""{"properties":{"message":{"type":"string"}}}"""),
        };
        NodeDescriptor[] catalog = [Descriptor("osc.udp.receive", EPortValueKind.Json) with { CanEvaluateStatically = false }, log];
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, catalog, new("a", "argument1", "b", "message")));
        Assert.Equal(tag is "s", GraphConnectionPolicy.CanConnect(document, catalog, new("a", "argument1", "b", "$settings.message")));
    }

    /// <summary>OSCパケット全体や一般的な配列・Objectを本文へ直接接続できます。</summary>
    [Theory]
    [InlineData("osc.udp.receive", "packet", "object")]
    [InlineData("osc.tcp.listen", "packet", "object")]
    [InlineData("value.array", "value", "array")]
    [InlineData("value.keyValue", "value", "object")]
    public void StructuredValuesCanConnectDirectlyToLogMessage(string type, string port, string dataType)
    {
        WorkflowDefinition document = new("flow", "flow", [Node("source", type), Node("log", "control.log")], []);
        NodeDescriptor source = Descriptor(type, EPortValueKind.Json, port) with { Outputs = [new(port, EPortValueKind.Json) { DataType = new(dataType) }] };
        NodeDescriptor target = Descriptor("control.log", EPortValueKind.Any) with { Inputs = [new("message", EPortValueKind.Json)] };
        Assert.True(GraphConnectionPolicy.CanConnect(document, [source, target], new("source", port, "log", "message")));
    }

    /// <summary>名前付き出力と無関係な設定接続でBinaryの伝播を失いません。</summary>
    [Theory]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Json, false)]
    [InlineData(EPortValueKind.Binary, EPortValueKind.Binary, true)]
    [InlineData(EPortValueKind.Json, EPortValueKind.Binary, false)]
    [InlineData(EPortValueKind.Any, EPortValueKind.Binary, true)]
    public void AnyPassRetainsUpstreamKind(EPortValueKind sourceKind, EPortValueKind destinationKind, bool allowed)
    {
        WorkflowDefinition document = new("flow", "flow", [Node("source", "source"), Node("pass", "control.delay"), Node("target", "target"), Node("setting", "value.constant")],
            [new("source", "payload", "pass", "value"), new("setting", "value", "pass", "$settings.milliseconds")]);
        NodeDescriptor[] catalog = [Descriptor("source", sourceKind, "payload"), Descriptor("control.delay", EPortValueKind.Any), Descriptor("target", destinationKind)];
        Assert.Equal(allowed, WorkflowConnectionTypes.CanConnect(document, catalog, new("pass", "value", "target", "value")));
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, catalog, new("pass", "value", "target", "value")));
    }

    /// <summary>JSON内部抽出はANY出力でもBinaryにならず、不正Pointerも拒否します。</summary>
    [Theory]
    [InlineData("/value", EPortValueKind.Binary, false)]
    [InlineData("/value", EPortValueKind.Json, true)]
    [InlineData("/~2", EPortValueKind.Any, false)]
    [InlineData("value", EPortValueKind.Any, false)]
    [InlineData("", EPortValueKind.Binary, true)]
    public void ExtractionUsesJsonKindAndValidPointer(string path, EPortValueKind targetKind, bool allowed)
    {
        WorkflowDefinition document = new("flow", "flow", [Node("a", "source"), Node("b", "target")], []);
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, [Descriptor("source", EPortValueKind.Any), Descriptor("target", targetKind)], new("a", "value", "b", "value", Path: path)));
    }

    /// <summary>ANY入力でも組込みが受け入れない既知の値は接続時に拒否します。</summary>
    [Theory]
    [InlineData("value.array", "item", EPortValueKind.Binary, false)]
    [InlineData("value.keyValue", "value", EPortValueKind.Binary, false)]
    [InlineData("data.convert", "value", EPortValueKind.Binary, false)]
    [InlineData("data.json.parse", "value", EPortValueKind.Binary, true)]
    [InlineData("data.binary.parse", "value", EPortValueKind.Binary, true)]
    [InlineData("data.json.serialize", "value", EPortValueKind.Binary, false)]
    public void BuiltinAnyInputsHonorTheirRuntimeContract(string targetType, string port, EPortValueKind sourceKind, bool allowed)
    {
        WorkflowDefinition document = new("flow", "flow", [Node("a", "source"), Node("b", targetType)], []);
        NodeDescriptor target = Descriptor(targetType, EPortValueKind.Any) with { Inputs = [new(port)] };
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, [Descriptor("source", sourceKind), target], new("a", "value", "b", port)));
    }

    /// <summary>動的な配列も設定へ接続し、静的制約が明示された項目だけを制限します。</summary>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void SettingsAcceptRuntimeDependenciesUnlessDeclaredStatic(bool pure, bool staticOnly, bool allowed)
    {
        WorkflowDefinition document = new("flow", "flow", [Node("a", "source"), Node("b", "value.array"), Node("c", "target")], [new("a", "value", "b", "item")]);
        NodeDescriptor target = Descriptor("target", EPortValueKind.Json) with { SettingsSchema = JsonSerializer.SerializeToElement(new { properties = new { values = new { type = "array" } } }) };
        if (staticOnly)
        {
            target = target with { SettingsSchema = JsonSerializer.Deserialize<JsonElement>("""{"properties":{"values":{"type":"array","x-runtime-input":false}}}""") };
        }
        NodeDescriptor[] catalog = [Descriptor("source", EPortValueKind.Any) with { CanEvaluateStatically = pure }, Descriptor("value.array", EPortValueKind.Json) with { CanEvaluateStatically = true }, target];
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, catalog, new("b", "value", "c", "$settings.values")));
    }

    /// <summary>既存線を置換して循環を作らず、結果envelopeを数値と誤認しません。</summary>
    [Fact]
    public void CycleAndResultEnvelopeAreRejected()
    {
        WorkflowDefinition document = new("flow", "flow", [Node("a", "control.pass"), Node("b", "control.pass"), Node("c", "math.add")], [new("a", "value", "b", "value")]);
        NodeDescriptor[] catalog = [Descriptor("control.pass", EPortValueKind.Any), Descriptor("math.add", EPortValueKind.Json) with { Inputs = [new("left", EPortValueKind.Json)] }];
        Assert.False(GraphConnectionPolicy.CanConnect(document, catalog, new("b", "value", "a", "value")));
        Assert.False(GraphConnectionPolicy.CanConnect(document, catalog, new("a", "result", "c", "left")));
        Assert.True(GraphConnectionPolicy.CanConnect(document, catalog, new("a", "result", "c", "trigger")));
    }

    /// <summary>ANYへ接続する要素も明示した配列型に従います。</summary>
    [Theory]
    [InlineData("42", "item", false)]
    [InlineData("\"text\"", "item", true)]
    [InlineData("[42]", "items", false)]
    [InlineData("[\"text\"]", "items", true)]
    public void ExplicitArrayTypeAppliesToSingleAndMultipleItems(string text, string port, bool allowed)
    {
        NodeDefinition source = Node("a", "value.constant") with { Settings = JsonSerializer.SerializeToElement(new { type = "auto", text }) };
        NodeDefinition target = Node("b", "value.array") with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["elementType"] = NodeValue.FromObject(new { type = "string" }) } };
        WorkflowDefinition document = new("flow", "flow", [source, target], []);
        NodeDescriptor array = Descriptor("value.array", EPortValueKind.Json) with { Inputs = [new("item"), new("items", EPortValueKind.Json), new("elementType", EPortValueKind.Json)] };
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, [Descriptor("value.constant", EPortValueKind.Json), array], new("a", "value", "b", port)));
    }

    /// <summary>論理演算では左右の真偽値と整数を混在させません。</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("42", false)]
    public void LogicChecksBothOperands(string text, bool allowed)
    {
        NodeDefinition source = Node("a", "value.constant") with { Settings = JsonSerializer.SerializeToElement(new { type = "auto", text }) };
        NodeDefinition target = Node("b", "math.and") with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["right"] = NodeValue.FromObject(true) } };
        WorkflowDefinition document = new("flow", "flow", [source, target], []);
        NodeDescriptor logic = Descriptor("math.and", EPortValueKind.Json) with { Inputs = [new("left", EPortValueKind.Json), new("right", EPortValueKind.Json)] };
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, [Descriptor("value.constant", EPortValueKind.Json), logic], new("a", "value", "b", "left")));
    }

    /// <summary>値を実行しない最小のノードです。</summary>
    /// <returns>草稿のノードです。</returns>
    private static NodeDefinition Node(string id, string type) => new(id, type, JsonSerializer.SerializeToElement(new { }));

    /// <summary>入出力区分だけを持つカタログです。</summary>
    /// <returns>公開契約です。</returns>
    private static NodeDescriptor Descriptor(string type, EPortValueKind kind, string output = "value") => new(type, type, "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [new("value", kind)], [new(output, kind)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
