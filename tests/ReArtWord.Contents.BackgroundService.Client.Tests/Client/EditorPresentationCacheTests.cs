using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>表示解析の再利用が編集、Undo、型変更、未確定入力を隠さないことを確認します。</summary>
public sealed class EditorPresentationCacheTests
{
    /// <summary>プロパティ追加・削除を、個別出力と型と接続値へ同時に反映します。</summary>
    [Fact]
    public void EditsAndUndoReplacePropertyPortsTypesAndPreview()
    {
        EditorPresentationCache cache = new();
        WorkflowDefinition initial = Document("{\"age\":10}");
        NodeDescriptor[] catalog = Catalog();
        GraphTypeInference first = cache.Graph(initial, catalog);
        Assert.Contains(first.EditorDescriptor("expand")!.Outputs, port => string.Equals(port.Name, "value/age", StringComparison.Ordinal));
        Assert.Equal("integer", first.Port("expand", "value/age", true).InferredType);
        Assert.Equal(10, cache.Preview(Draft(initial)).Input(initial.Nodes[1], "value")!.Json.GetProperty("age").GetInt32());

        WorkflowDefinition edited = Document("{\"name\":\"new\"}");
        GraphTypeInference next = cache.Graph(edited, catalog);
        Assert.DoesNotContain(next.EditorDescriptor("expand")!.Outputs, port => string.Equals(port.Name, "value/age", StringComparison.Ordinal));
        Assert.Contains(next.EditorDescriptor("expand")!.Outputs, port => string.Equals(port.Name, "value/name", StringComparison.Ordinal));
        Assert.Equal("string", next.Port("expand", "value/name", true).InferredType);
        Assert.Equal("new", cache.Preview(Draft(edited)).Input(edited.Nodes[1], "value")!.Json.GetProperty("name").GetString());

        GraphTypeInference restored = cache.Graph(initial, catalog);
        Assert.Equal("integer", restored.Port("expand", "value/age", true).InferredType);
        Assert.DoesNotContain(restored.EditorDescriptor("expand")!.Outputs, port => string.Equals(port.Name, "value/name", StringComparison.Ordinal));
    }

    /// <summary>文書本文が変わらない不正入力中も旧確定値を見せず、訂正後は復帰します。</summary>
    [Fact]
    public void InvalidInputAndDisconnectionCannotReuseAnOldPreview()
    {
        EditorPresentationCache cache = new();
        WorkflowDefinition document = Document("{\"age\":10}");
        EditorDraft draft = Draft(document);
        Assert.NotNull(cache.Preview(draft).Input(document.Nodes[1], "value"));
        EditorDraft invalid = draft with { InputDrafts = new Dictionary<string, string>(StringComparer.Ordinal) { ["source/settings/text"] = "{" } };
        Assert.Null(cache.Preview(invalid).Input(document.Nodes[1], "value"));
        Assert.NotNull(cache.Preview(draft).Input(document.Nodes[1], "value"));
        WorkflowDefinition disconnected = document with { Wires = [] };
        Assert.Null(cache.Preview(Draft(disconnected)).Input(disconnected.Nodes[1], "value"));
    }

    /// <summary>同じList実体のカタログ更新と同じIDの別ワークフローを取り違えません。</summary>
    [Fact]
    public void MutableCatalogAndReplacedDocumentInvalidateDeclarations()
    {
        NodeDefinition unknown = new("source", "extension", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition document = new("same-id", "same-id", [unknown], []);
        List<NodeDescriptor> catalog = [Descriptor("extension", [], [new("value", EPortValueKind.Binary)])];
        EditorPresentationCache cache = new();
        Assert.Equal("binary", cache.Graph(document, catalog).Port("source", "value", true).InferredType);
        catalog[0] = catalog[0] with { Outputs = [new("value", EPortValueKind.Json)] };
        Assert.Equal("json", cache.Graph(document, catalog).Port("source", "value", true).Key);
        Assert.Null(cache.Graph(document, catalog).Port("source", "value", true).InferredType);
        WorkflowDefinition replaced = Document("42") with { Id = document.Id };
        Assert.Equal("integer", cache.Graph(replaced, catalog).Port("source", "value", true).InferredType);
    }

    /// <summary>値だけの上流とObject展開を接続した編集対象です。</summary>
    /// <returns>外部処理を含まない定義です。</returns>
    internal static WorkflowDefinition Document(string text) => new("cache", "cache",
        [new("source", "value.constant", JsonSerializer.SerializeToElement(new { text }), Position: new(500, 100)),
         new("expand", "data.object.expand", JsonSerializer.SerializeToElement(new { }), Position: new(100, 200))],
        [new("source", "value", "expand", "value")]);

    /// <summary>実行用の既定ポートだけを宣言します。</summary>
    /// <returns>動的プロパティは推測側が追加するカタログです。</returns>
    internal static NodeDescriptor[] Catalog() => [Descriptor("value.constant", [], [new("value", EPortValueKind.Any)]), Descriptor("data.object.expand", [new("value", EPortValueKind.Json)], [new("value", EPortValueKind.Json)])];

    /// <summary>編集状態の比較元を固定します。</summary>
    /// <returns>保存済みの草稿です。</returns>
    private static EditorDraft Draft(WorkflowDefinition document) => new(document, document, "base", document, null, "base", false, []);

    /// <summary>型推測と表示に必要な最小の宣言です。</summary>
    /// <returns>テスト用カタログ項目です。</returns>
    private static NodeDescriptor Descriptor(string type, IReadOnlyList<PortDescriptor> inputs, IReadOnlyList<PortDescriptor> outputs)
        => new(type, type, "Values", type, ENodeKind.Action, EExecutionLocation.Independent, inputs, outputs, [], JsonSerializer.SerializeToElement(new { }));
}
