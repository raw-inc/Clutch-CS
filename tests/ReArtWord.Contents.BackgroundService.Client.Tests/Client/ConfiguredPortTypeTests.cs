using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>設定の接続、型定義の合成、抽出の欠損を含む表示を検証します。</summary>
public sealed class ConfiguredPortTypeTests
{
    /// <summary>接続された設定を保存済みの設定より優先し、不明な接続では旧値へ戻りません。</summary>
    [Theory]
    [InlineData("value.constant", "type", "integer", "string")]
    [InlineData("data.convert", "targetType", "integer", "string")]
    public void ConnectedTypeSettingsOverrideSavedValues(string type, string setting, string saved, string supplied)
    {
        NodeDefinition name = Node("name", "value.constant", new { type = "string", text = supplied });
        NodeDefinition target = Node("target", type, new Dictionary<string, object>(StringComparer.Ordinal) { [setting] = saved, ["text"] = "42" });
        WireDefinition wire = new("name", "value", "target", SettingPorts.Name(setting));
        Assert.Equal("string", Create([name, target], [wire]).Port("target", "value", true).InferredType);
        NodeDefinition unknown = name with { Type = "extension.dynamic" };
        Assert.Null(Create([unknown, target], [wire]).Port("target", "value", true).InferredType);
    }

    /// <summary>テキスト設定の接続を解析し、後続ノードまで具体型を伝えます。</summary>
    [Fact]
    public void ConnectedTextDeterminesConstantAndDownstreamType()
    {
        NodeDefinition text = Node("text", "value.constant", new { type = "string", text = "[true,false]" });
        NodeDefinition value = Node("value", "value.constant", new { type = "auto", text = "42" });
        NodeDefinition pass = Node("pass", "control.pass", new { });
        GraphTypeInference inference = Create([text, value, pass], [new("text", "value", "value", "$settings.text"), new("value", "value", "pass", "value")]);
        GraphPortPresentation result = inference.Port("pass", "value", true);
        Assert.Equal("array", result.Key);
        Assert.Equal("boolean", result.DataType!.ElementType!.Type);
        Assert.Equal("Array of Boolean", result.DisplayLabel(new(new(new BrowserTestRuntime()))));
    }

    /// <summary>型定義を項目として組み立てても、HTTP入力の項目型を取得できます。</summary>
    [Fact]
    public void ComposedDescriptorsKeepOptionalProperties()
    {
        NodeDefinition name = Node("name", "value.constant", new { type = "string", text = "integer" });
        NodeDefinition itemType = Node("itemType", "value.type", new { type = "string", optional = true });
        NodeDefinition property = Node("property", "value.keyValue", new { key = "age" });
        NodeDefinition bodyType = Node("bodyType", "value.type", new { type = "object" });
        NodeDefinition http = Node("http", "http.listen", new { });
        GraphTypeInference inference = Create([name, itemType, property, bodyType, http],
        [
            new("name", "value", "itemType", "$settings.type"), new("itemType", "value", "property", "value"),
            new("property", "value", "bodyType", "properties"), new("bodyType", "value", "http", "bodyType"),
        ]);
        GraphPortPresentation selected = inference.Wire(new("http", "body", "sink", "value", Path: "/age"));
        Assert.Equal("integer", selected.InferredType);
        Assert.True(selected.DataType!.Optional);
        Assert.Equal("object", inference.Port("http", "body", true).InferredType);
        Assert.Equal("object", inference.Port("http", "query", true).InferredType);
    }

    /// <summary>実行時に組み立てるオブジェクトも、キーと値の型が分かれば抽出に使えます。</summary>
    [Fact]
    public void ObjectPropertiesAndArrayIndexesPropagateWithoutExecutingNodes()
    {
        NodeDefinition request = Node("request", "http.request", new { });
        NodeDefinition property = Node("property", "value.keyValue", new { key = "response" });
        NodeDefinition array = Node("array", "value.array", new { });
        GraphTypeInference inference = Create([request, property, array], [new("request", "value", "property", "value"), new("property", "value", "array", "item")]);
        GraphPortPresentation selected = inference.Wire(new("array", "value", "sink", "value", Path: "/100/response"));
        Assert.Equal("object", selected.InferredType);
        Assert.True(selected.DataType!.Optional);
        Assert.Null(inference.Wire(new("array", "value", "sink", "value", Path: "/01/response")).InferredType);
        GraphPortPresentation missing = inference.Wire(new("array", "value", "sink", "value", Path: "/0/missing"));
        Assert.Equal("object", missing.InferredType);
        Assert.True(missing.DataType!.Optional);
    }

    /// <summary>返信を待つ設定が未確定なら、送信確認の型で代用しません。</summary>
    [Fact]
    public void TcpReplySettingDoesNotUseAnOldFallback()
    {
        NodeDefinition tcp = Node("tcp", "tcp.request", new { awaitReply = false });
        Assert.Equal("object", Create([tcp], []).Port("tcp", "value", true).InferredType);
        NodeDefinition yes = Node("yes", "value.constant", new { text = "true" });
        WireDefinition wire = new("yes", "value", "tcp", "$settings.awaitReply");
        Assert.Equal("binary", Create([tcp, yes], [wire]).Port("tcp", "value", true).InferredType);
        Assert.Null(Create([tcp, yes with { Type = "extension.dynamic" }], [wire]).Port("tcp", "value", true).InferredType);
    }

    /// <summary>カタログにも、設定に依存しない論理型を表示します。</summary>
    [Theory]
    [InlineData("file.read", "binary", "Bytes")]
    [InlineData("file.exists", "boolean", "Boolean")]
    [InlineData("directory.list", "array", "Array of Object")]
    [InlineData("ai.text.generate", "string", "Text")]
    [InlineData("ai.picture.generate", "object", "Object")]
    [InlineData("data.csv.parse", "array", "Array of Array of Text")]
    [InlineData("value.type", "type-definition", "Type definition")]
    public void CatalogAndGraphAgreeOnFixedTypes(string type, string family, string label)
    {
        NodeDefinition node = Node("node", type, new { });
        UiText ui = new(new(new BrowserTestRuntime()));
        GraphPortPresentation catalog = GraphPresentation.Port(Descriptor(type), "value", true);
        GraphPortPresentation graph = Create([node], []).Port(node.Id, "value", true);
        Assert.Equal(family, catalog.Key);
        Assert.Equal(label, catalog.DisplayLabel(ui));
        Assert.Equal(catalog, graph);
    }

    /// <summary>型設定の循環と、似た名前の拡張を組込みノードとして扱いません。</summary>
    [Fact]
    public void CyclicSettingsAndUnknownExtensionsStayUnresolved()
    {
        NodeDefinition value = Node("value", "value.constant", new { text = "42" });
        NodeDefinition extension = Node("extension", "file.read.custom", new { });
        GraphTypeInference inference = Create([value, extension], [new("value", "value", "value", "$settings.text")]);
        Assert.Null(inference.Port("value", "value", true).InferredType);
        Assert.Null(inference.Port("extension", "value", true).InferredType);
    }

    /// <summary>設定と固定入力を持つ検証用ノードです。</summary>
    /// <returns>元の入力を変更しない定義です。</returns>
    private static NodeDefinition Node(string id, string type, object settings) => new(id, type, JsonSerializer.SerializeToElement(settings));

    /// <summary>推測前の汎用入出力を持つ宣言です。</summary>
    /// <returns>固定型と汎用型の差を確認するための宣言です。</returns>
    private static NodeDescriptor Descriptor(string type) => new(type, type, "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [new("value")], [new("value"), new("body"), new("query")], [], JsonSerializer.SerializeToElement(new { }));

    /// <summary>一つの編集状態に対応する推測器を作ります。</summary>
    /// <returns>型と表示の共通推測です。</returns>
    private static GraphTypeInference Create(IReadOnlyList<NodeDefinition> nodes, IReadOnlyList<WireDefinition> wires)
        => new(new("workflow", "workflow", nodes, wires), nodes.Select(node => Descriptor(node.Type)).ToArray());
}
