using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>表示上の推測が、宣言や不明なデータを確定型へ置換しないことを確認します。</summary>
public sealed class GraphTypeInferenceTests
{
    /// <summary>明示型は定数設定より優先され、表示と色は値の種類に対応します。</summary>
    [Fact]
    public void ExplicitTypeInputWinsAndDeclarationRemains()
    {
        NodeDefinition constant = Node("constant", "value.constant", "{\"text\":\"42\",\"type\":\"string\"}") with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["type"] = Value("{\"type\":\"integer\"}") },
        };
        GraphTypeInference inference = Create([constant, Node("pass", "control.pass")], [new("constant", "value", "pass", "value")]);
        GraphPortPresentation port = inference.Port("pass", "value", true);
        Assert.Equal("port.type.integer", port.Label);
        Assert.Equal("integer", port.InferredType);
        Assert.Equal("integer", port.Key);
    }

    /// <summary>型ノード接続と型定義の表示を、実データのオブジェクトと区別します。</summary>
    [Fact]
    public void TypeNodeConnectionIsResolved()
    {
        GraphTypeInference inference = Create([Node("type", "value.type", "{\"type\":\"byte\"}"), Node("constant", "value.constant", "{\"text\":\"42\",\"type\":\"string\"}")], [new("type", "value", "constant", "type")]);
        Assert.Equal("byte", inference.Port("constant", "value", true).InferredType);
        Assert.Equal("type definition", inference.Port("type", "value", true).InferredType);
    }

    /// <summary>混在配列の要素型を追加itemだけから誤認しません。</summary>
    [Fact]
    public void MixedArrayDoesNotClaimAnElementType()
    {
        NodeDefinition array = Node("array", "value.array") with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["items"] = Value("[1]"), ["item"] = Value("\"text\"") },
        };
        GraphTypeInference inference = Create([array, Node("pass", "control.pass")], []);
        Assert.Equal("array", inference.Port("array", "value", true).InferredType);
        Assert.Null(inference.Wire(new("array", "value", "pass", "value", Path: "/0")).InferredType);
        Assert.Null(ValueTypeInference.FromJson(Value("[1,\"text\"]").Json, 0)!.ElementType);
    }

    /// <summary>循環と未確定型は保留し、nullを許す既知型はその条件を保持します。</summary>
    [Fact]
    public void CyclesUnknownAndNullableRemainUnresolved()
    {
        NodeDefinition constant = Node("nullable", "value.constant", "{\"text\":\"42\"}") with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["type"] = Value("{\"type\":\"integer\",\"optional\":true}") },
        };
        GraphTypeInference inference = Create([constant, Node("a", "control.pass"), Node("b", "control.pass"), Node("unknown", "extension")], [new("a", "value", "b", "value"), new("b", "value", "a", "value")]);
        Assert.Null(inference.Port("a", "value", true).InferredType);
        Assert.Null(inference.Port("unknown", "value", true).InferredType);
        Assert.Equal("integer", inference.Port("nullable", "value", true).InferredType);
        Assert.True(inference.Port("nullable", "value", true).DataType!.Optional);
    }

    /// <summary>HTTPの項目型と欠損の可能性を、未知の項目から区別します。</summary>
    [Fact]
    public void HttpPropertiesAndUnknownExtractionRemainDistinct()
    {
        NodeDefinition http = Node("http", "http.listen") with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["queryType"] = Value("{\"type\":\"object\",\"properties\":{\"age\":{\"type\":\"integer\"},\"nickname\":{\"type\":\"string\",\"optional\":true}}}") },
        };
        GraphTypeInference inference = Create([http], []);
        WireDefinition wire = new("http", "query", "sink", "value", Path: "/age");
        Assert.Equal("integer", inference.Wire(wire).InferredType);
        Assert.Equal("string", inference.Wire(wire with { Path = "/nickname" }).InferredType);
        Assert.True(inference.Wire(wire with { Path = "/nickname" }).DataType!.Optional);
        Assert.Null(inference.Wire(wire with { Path = "/missing" }).InferredType);
        Assert.Null(inference.Wire(wire with { Path = "/~2" }).InferredType);
    }

    /// <summary>不正な重複プロパティと大きい入力でも有限に保守的な結果へ戻ります。</summary>
    [Fact]
    public void DuplicateAndLargeJsonDoNotBreakRendering()
    {
        Assert.Null(ValueTypeInference.FromJson(Value("{\"x\":1,\"x\":2}").Json, 0)!.Properties);
        Assert.Null(ValueTypeInference.Descriptor(Value("{\"type\":\"integer\",\"type\":\"string\"}")));
        JsonElement large = JsonSerializer.SerializeToElement(Enumerable.Range(0, 10000).ToArray());
        Assert.Null(ValueTypeInference.FromJson(large, 0)!.ElementType);
        Assert.Null(ValueTypeInference.FromJson(large, 64));
    }

    /// <summary>演算の成功型とパススルーのバイト列を区別します。</summary>
    [Fact]
    public void ArithmeticAndBinaryHaveTheirOwnFamilies()
    {
        NodeDefinition pass = Node("pass", "control.pass") with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromBytes(new byte[] { 1 }) } };
        GraphTypeInference inference = Create([pass, Node("add", "math.add")], []);
        Assert.Equal("number", inference.Port("add", "value", true).InferredType);
        Assert.Equal("binary", inference.Port("pass", "value", true).Key);
    }

    /// <summary>比較は真偽値、累乗とルートは数値として後続ANYへ伝わります。</summary>
    [Theory]
    [InlineData("math.compare", "boolean")]
    [InlineData("math.equal", "boolean")]
    [InlineData("math.notEqual", "boolean")]
    [InlineData("math.power", "number")]
    [InlineData("math.root", "number")]
    public void MathOutputTypesPropagateToConnectedInputs(string type, string expected)
    {
        GraphTypeInference inference = Create([Node("math", type), Node("pass", "control.pass")], [new("math", "value", "pass", "value")]);
        Assert.Equal(expected, inference.Port("math", "value", true).InferredType);
        Assert.Equal(expected, inference.Port("pass", "value", false).InferredType);
    }

    /// <summary>論理演算の入力から真偽値と整数を区別し、配列も要素型で判断します。</summary>
    [Theory]
    [InlineData("math.and", "{\"left\":true,\"right\":false}", "boolean")]
    [InlineData("math.and", "{\"left\":7,\"right\":3}", "integer")]
    [InlineData("math.or", "{\"left\":true,\"right\":false}", "boolean")]
    [InlineData("math.or", "{\"left\":1,\"right\":2}", "integer")]
    [InlineData("math.not", "{\"value\":true}", "boolean")]
    [InlineData("math.not", "{\"value\":1}", "integer")]
    [InlineData("math.nand", "{\"left\":true,\"right\":true}", "boolean")]
    [InlineData("math.nand", "{\"left\":7,\"right\":3}", "integer")]
    [InlineData("math.nor", "{\"left\":false,\"right\":false}", "boolean")]
    [InlineData("math.nor", "{\"left\":1,\"right\":2}", "integer")]
    [InlineData("math.xor", "{\"left\":true,\"right\":false}", "boolean")]
    [InlineData("math.xor", "{\"left\":1,\"right\":3}", "integer")]
    [InlineData("math.ands", "{\"value\":[true,false,true]}", "boolean")]
    [InlineData("math.ands", "{\"value\":[7,3,1]}", "integer")]
    [InlineData("math.ors", "{\"value\":[true,false,true]}", "boolean")]
    [InlineData("math.ors", "{\"value\":[1,2,4]}", "integer")]
    [InlineData("math.and", "{\"left\":true,\"right\":1}", null)]
    [InlineData("math.ands", "{\"value\":[true,1]}", null)]
    public void LogicTypesFollowTheInputValues(string type, string inputs, string? expected)
    {
        using JsonDocument parsed = JsonDocument.Parse(inputs);
        NodeDefinition logic = Node("logic", type) with
        {
            Inputs = parsed.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => NodeValue.FromJson(property.Value), StringComparer.Ordinal),
        };
        GraphTypeInference inference = Create([logic, Node("pass", "control.pass")], [new("logic", "value", "pass", "value")]);
        Assert.Equal(expected, inference.Port("logic", "value", true).InferredType);
        Assert.Equal(expected, inference.Port("pass", "value", false).InferredType);
    }

    /// <summary>型表示のための最小定義です。</summary>
    /// <returns>変更前の定義です。</returns>
    private static NodeDefinition Node(string id, string type, string settings = "{}") => new(id, type, JsonSerializer.Deserialize<JsonElement>(settings));

    /// <summary>所有JSONを作ります。</summary>
    /// <returns>リテラル値です。</returns>
    private static NodeValue Value(string json) => NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json));

    /// <summary>ANY契約のまま成功型の補足を確認します。</summary>
    /// <returns>描画一回の推測器です。</returns>
    private static GraphTypeInference Create(IReadOnlyList<NodeDefinition> nodes, IReadOnlyList<WireDefinition> wires)
    {
        NodeDescriptor[] catalog = nodes.Select(node => new NodeDescriptor(node.Type, node.Type, "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [new("value"), new("type")], [new("value"), new("query")], [], JsonSerializer.Deserialize<JsonElement>("{}"))).ToArray();
        return new(new("test", "test", nodes, wires), catalog);
    }
}
