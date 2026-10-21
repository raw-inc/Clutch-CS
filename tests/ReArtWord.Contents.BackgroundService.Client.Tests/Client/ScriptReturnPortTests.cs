using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>返却型の表示と後続への伝播を、JSONの通信区分から分けて検証します。</summary>
public sealed class ScriptReturnPortTests
{
    /// <summary>バイナリの返却型をそのまま表示し、次のC#バイト配列入力へ接続できます。</summary>
    [Fact]
    public void BinaryReturnTypeAndIndividualArgumentRemainBinaryInEditor()
    {
        string code = "public static byte[] Run() => new byte[] { 1 };";
        NodeDefinition source = Script("csharp", code, new("binary"));
        string consumerCode = "public static byte[] Run(byte[] data) => data;";
        ScriptAnalysis report = new(ContractJson.Hash(new ScriptAnalysisRequest("csharp", consumerCode)),
            new(new Dictionary<string, ValueTypeDefinition> { ["data"] = new("binary") }, new("binary")), [], ValueProtocolVersion: 1);
        NodeDefinition target = new("consumer", "script.csharp", JsonSerializer.SerializeToElement(new { code = consumerCode, analysis = report, inputMode = "individual" }, ContractJson.Options));
        WireDefinition wire = new(source.Id, "value", target.Id, ScriptInputPorts.Name("data"));
        NodeDescriptor descriptor = Descriptor(source.Type) with { Outputs = [new("value", EPortValueKind.Any)] };
        GraphTypeInference graph = new(new("sample", "Sample", [source, target], [wire]), [descriptor]);
        Assert.Equal("binary", graph.Port(source.Id, "value", true).InferredType);
        Assert.Equal("binary", graph.Wire(wire).InferredType);
        Assert.Equal("binary", graph.Port(target.Id, ScriptInputPorts.Name("data"), false).InferredType);
        Assert.Equal(EPortValueKind.Any, Assert.Single(ScriptInputPorts.Resolve(descriptor, target).Inputs, port => string.Equals(port.Name, ScriptInputPorts.Name("data"), StringComparison.Ordinal)).ValueKind);
    }

    /// <summary>言語や引数の有無によらず、現在の返却型をポート・配線・接続先へ反映します。</summary>
    [Theory]
    [InlineData("csharp", "public static int Run() {return 0;}", "integer")]
    [InlineData("csharp", "public static long Run() => 1;", "int64")]
    [InlineData("csharp", "public static string Run() => \"hello\";", "string")]
    [InlineData("python", "def run() -> int: return 0", "integer")]
    [InlineData("python", "def run() -> str: return 'hello'", "string")]
    [InlineData("javascript", "export function run() {return 0;}", "number")]
    [InlineData("javascript", "export function run() {return 'hello';}", "string")]
    public void ScalarReturnTypePropagatesWithoutChangingJsonTransport(string language, string code, string resultType)
    {
        NodeDefinition script = Script(language, code, new(resultType));
        NodeDefinition sink = new("sink", "control.pass", JsonSerializer.SerializeToElement(new { }));
        WireDefinition wire = new(script.Id, "value", sink.Id, "value");
        NodeDescriptor descriptor = Descriptor(script.Type);
        WorkflowDefinition document = new("sample", "Sample", [script, sink], [wire]);
        GraphTypeInference graph = new(document, [descriptor, Descriptor(sink.Type)]);
        Assert.Equal(resultType, graph.Port(script.Id, "value", true).InferredType);
        Assert.Equal(resultType, graph.Wire(wire).InferredType);
        Assert.Equal(resultType, graph.Port(sink.Id, "value", false).InferredType);
        Assert.Equal(EPortValueKind.Json, descriptor.Outputs[0].ValueKind);

        NodeDefinition edited = script with { Settings = JsonSerializer.SerializeToElement(new { code = code + " ", analysis = script.Settings.GetProperty("analysis"), inputMode = "individual" }) };
        GraphTypeInference pending = new(document with { Nodes = [edited, sink] }, [descriptor, Descriptor(sink.Type)]);
        Assert.Equal("any", pending.Port(script.Id, "value", true).Key);
        Assert.Null(pending.Port(script.Id, "value", true).InferredType);
    }

    /// <summary>オブジェクトの返却型から個別出力を作り、配列要素やnullableも失いません。</summary>
    [Fact]
    public void ObjectPropertiesAndArrayElementsRetainTheirReturnTypes()
    {
        ValueTypeDefinition result = new("object", Properties: new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal)
        {
            ["count"] = new("integer"), ["names"] = new("array", new("string", Optional: true)),
        });
        NodeDefinition script = Script("javascript", "export function run() { return {count:1,names:['a',null]}; }", result);
        GraphTypeInference graph = new(new("sample", "Sample", [script], []), [Descriptor(script.Type)]);
        Assert.Contains(graph.EditorDescriptor(script.Id)!.Outputs, port => string.Equals(port.Name, "value/count", StringComparison.Ordinal));
        Assert.Equal("integer", graph.Port(script.Id, "value/count", true).InferredType);
        Assert.Equal("string", graph.Port(script.Id, "value/names", true).DataType!.ElementType!.Type);
        Assert.True(graph.Port(script.Id, "value/names", true).DataType!.ElementType!.Optional);
    }

    /// <summary>画面が受信した型契約と同じ指紋を持つ、引数なしの保存済みノードです。</summary>
    /// <returns>現在コードに対応した型情報を持つ定義です。</returns>
    private static NodeDefinition Script(string language, string code, ValueTypeDefinition result)
    {
        ScriptAnalysis report = new(ContractJson.Hash(new ScriptAnalysisRequest(language, code)), new(new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal), result), []);
        return new("script", "script." + language, JsonSerializer.SerializeToElement(new { code, analysis = report, inputMode = "individual" }, ContractJson.Options));
    }

    /// <summary>伝送のJSON区分しか持たない元カタログです。</summary>
    /// <returns>表示時の推論結果を埋め込まない宣言です。</returns>
    private static NodeDescriptor Descriptor(string type) => new(type, "Script", "Scripting", "Script", ENodeKind.Action, EExecutionLocation.ExplicitPeer,
        [new("value", EPortValueKind.Json)], [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
