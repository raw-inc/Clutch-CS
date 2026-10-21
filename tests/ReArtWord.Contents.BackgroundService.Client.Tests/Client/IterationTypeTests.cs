using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>要素型の伝播と既知の不適合を、編集と共通検証の両境界で確認します。</summary>
public sealed class IterationTypeTests
{
    /// <summary>上限内の配列を全要素確認し、後半の異なる型を見落としません。</summary>
    [Fact]
    public void LargerArraysInspectTheLastElementBeforeDeclaringAType()
    {
        JsonElement integers = JsonSerializer.SerializeToElement(Enumerable.Range(0, 4096));
        Assert.Equal("integer", ValueTypeInference.FromJson(integers, 0)?.ElementType?.Type);
        List<object> mixed = Enumerable.Range(0, 128).Cast<object>().ToList();
        mixed.Add("different");
        Assert.Null(ValueTypeInference.FromJson(JsonSerializer.SerializeToElement(mixed), 0)?.ElementType);
    }

    /// <summary>空・混在配列は断定せず、既知の文字列要素を数値演算へ渡しません。</summary>
    [Theory]
    [InlineData("[1,2]", "integer", true)]
    [InlineData("[1.5,2.5]", "number", true)]
    [InlineData("[\"a\",\"b\"]", "string", false)]
    [InlineData("[true,false]", "boolean", false)]
    [InlineData("[]", null, true)]
    [InlineData("[1,\"b\"]", null, true)]
    [InlineData("[null,1]", null, true)]
    public void ArrayElementTypeControlsNumericConnections(string json, string? expectedType, bool allowed)
    {
        foreach (string type in new[] { IterationNodeTypes.ForEach, IterationNodeTypes.ParallelForEach })
        {
            WorkflowDefinition document = Document(json, type);
            WorkflowTypeInference shared = new(document, Catalog());
            GraphTypeInference display = new(document, Catalog());
            Assert.Equal(expectedType, shared.PortType("each", "item", true)?.Type);
            Assert.Equal(expectedType, display.Port("each", "item", true).InferredType);
            WireDefinition wire = new("each", "item", "math", "left");
            Assert.Equal(allowed, WorkflowConnectionTypes.CanConnect(document, Catalog(), wire));
            Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, Catalog(), wire));
            Assert.Equal("integer", display.Port("each", "index", true).InferredType);
            Assert.Equal("integer", display.Port("each", "count", true).InferredType);
        }
    }

    /// <summary>要素内の既知プロパティ型をPointer抽出後にも保持します。</summary>
    [Fact]
    public void ObjectElementsKeepPropertyTypes()
    {
        WorkflowDefinition document = Document("[{\"name\":\"a\",\"amount\":1},{\"name\":\"b\",\"amount\":2}]");
        WorkflowTypeInference inference = new(document, Catalog());
        WireDefinition name = new("each", "item", "math", "left", Path: "/name");
        Assert.Equal("string", inference.OutputType(name)?.Type);
        Assert.False(GraphConnectionPolicy.CanConnect(document, Catalog(), name));
        Assert.True(GraphConnectionPolicy.CanConnect(document, Catalog(), name with { Path = "/amount" }));
        Assert.True(GraphConnectionPolicy.CanConnect(document, Catalog(), name with { Path = "/missing" }));
    }

    /// <summary>Endが集める型は、対応する反復入力ではなく接続されたボディ結果で決まります。</summary>
    [Fact]
    public void EndCollectsBodyTypeAndFeedsAnotherIteration()
    {
        WorkflowDefinition source = Document("[1,2]");
        NodeDefinition text = Node("text", "value.constant") with { Settings = JsonSerializer.SerializeToElement(new { type = "string", text = "converted" }) };
        WorkflowDefinition document = source with
        {
            Nodes = [.. source.Nodes, text, Node("next", IterationNodeTypes.ForEach)],
            Wires = [.. source.Wires, new("each", "loop", "end", "loop"), new("text", "value", "end", "value"), new("end", "value", "next", "value")],
        };
        WorkflowTypeInference inference = new(document, Catalog());
        ValueTypeDefinition? result = inference.PortType("end", "value", true);
        Assert.Equal("array", result?.Type);
        Assert.Equal("string", result?.ElementType?.Type);
        Assert.Equal("string", inference.PortType("next", "item", true)?.Type);
        Assert.False(GraphConnectionPolicy.CanConnect(document, Catalog(), new("next", "item", "math", "left")));
    }

    /// <summary>反復境界は専用ポート同士だけで結び、空Pointerでも抽出を許しません。</summary>
    [Theory]
    [InlineData("each", "loop", "end", "loop", null, true)]
    [InlineData("each", "loop", "end", "loop", "", false)]
    [InlineData("each", "loop", "end", "loop", "/x", false)]
    [InlineData("each", "item", "end", "loop", null, false)]
    [InlineData("each", "loop", "end", "value", null, false)]
    [InlineData("each", "loop", "math", "trigger", null, false)]
    [InlineData("input", "value", "end", "loop", null, false)]
    public void LoopWireIsStructural(string from, string output, string to, string input, string? path, bool allowed)
    {
        WorkflowDefinition document = Document("[1]");
        WireDefinition wire = new(from, output, to, input, Path: path);
        Assert.Equal(allowed, WorkflowConnectionTypes.CanConnect(document, Catalog(), wire));
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, Catalog(), wire));
        Assert.Equal("port.type.loop", new GraphTypeInference(document, Catalog()).Port("each", "loop", true).Label);
    }

    /// <summary>未知の要素型で先に配線した場合も、後から文字列配列を入れて数値接続を壊しません。</summary>
    [Fact]
    public void UpstreamReplacementChecksNewlyKnownDownstreamType()
    {
        WorkflowDefinition source = Document("[\"text\"]");
        WorkflowDefinition document = source with { Wires = [new("each", "item", "math", "left")] };
        Assert.False(GraphConnectionPolicy.CanConnect(document, Catalog(), new("input", "value", "each", "value")));
        WorkflowDefinition unknown = Document("[]") with { Wires = document.Wires };
        Assert.True(GraphConnectionPolicy.CanConnect(unknown, Catalog(), new("input", "value", "each", "value")));
    }

    /// <summary>数値から真偽値への暗黙変換はせず、数値で成立する論理演算は残します。</summary>
    [Theory]
    [InlineData("[\"text\"]", "math.not", "value", false)]
    [InlineData("[1]", "math.not", "value", true)]
    [InlineData("[true]", "math.not", "value", true)]
    [InlineData("[{}]", "math.power", "base", false)]
    [InlineData("[[1,2]]", "math.ands", "value", true)]
    [InlineData("[[\"a\",\"b\"]]", "math.ands", "value", false)]
    public void BuiltinInputsRejectOnlyKnownWrongFamilies(string json, string targetType, string port, bool allowed)
    {
        WorkflowDefinition source = Document(json);
        WorkflowDefinition document = source with { Nodes = [.. source.Nodes.Where(node => !string.Equals(node.Id, "math", StringComparison.Ordinal)), Node("math", targetType)] };
        NodeDescriptor target = Descriptor(targetType, [new(port, EPortValueKind.Any)], [new("value", EPortValueKind.Json)]);
        Assert.Equal(allowed, GraphConnectionPolicy.CanConnect(document, [.. Catalog(), target], new("each", "item", "math", port)));
    }

    /// <summary>配列入力を用意し、未配置のEndを任意に配線できる最小文書です。</summary>
    /// <returns>実行評価を含まない型検証用の定義です。</returns>
    private static WorkflowDefinition Document(string json, string? type = null)
    {
        type ??= IterationNodeTypes.ForEach;
        NodeDefinition input = Node("input", "value.constant") with { Settings = JsonSerializer.SerializeToElement(new { type = "auto", text = json }) };
        return new("iteration", "iteration", [input, Node("each", type), Node("end", IterationNodeTypes.End), Node("math", "math.add")], [new("input", "value", "each", "value")]);
    }

    /// <summary>副作用を実行せず、カタログの型契約だけを用意します。</summary>
    /// <returns>既知の入力・出力です。</returns>
    private static NodeDescriptor[] Catalog() =>
    [
        Descriptor("value.constant", [], [new("value", EPortValueKind.Any)]),
        Descriptor(IterationNodeTypes.ForEach, [new("value", EPortValueKind.Json)], [new("item"), new("index", EPortValueKind.Json), new("count", EPortValueKind.Json), new("loop", EPortValueKind.Json)]),
        Descriptor(IterationNodeTypes.ParallelForEach, [new("value", EPortValueKind.Json)], [new("item"), new("index", EPortValueKind.Json), new("count", EPortValueKind.Json), new("loop", EPortValueKind.Json)]),
        Descriptor(IterationNodeTypes.End, [new("loop", EPortValueKind.Json), new("value", EPortValueKind.Json)], [new("value", EPortValueKind.Json)]),
        Descriptor("math.add", [new("left", EPortValueKind.Json), new("right", EPortValueKind.Json)], [new("value", EPortValueKind.Json)]),
    ];

    /// <summary>型推測が値を実行しないノード定義です。</summary>
    /// <returns>設定を変更しない試験用の構成です。</returns>
    private static NodeDefinition Node(string id, string type) => new(id, type, JsonSerializer.SerializeToElement(new { }));

    /// <summary>入出力以外の挙動を持たないカタログです。</summary>
    /// <returns>ノードのポート契約です。</returns>
    private static NodeDescriptor Descriptor(string type, IReadOnlyList<PortDescriptor> inputs, IReadOnlyList<PortDescriptor> outputs)
        => new(type, type, "Control", "", ENodeKind.Action, EExecutionLocation.Independent, inputs, outputs, ["success"], JsonSerializer.SerializeToElement(new { }));
}
