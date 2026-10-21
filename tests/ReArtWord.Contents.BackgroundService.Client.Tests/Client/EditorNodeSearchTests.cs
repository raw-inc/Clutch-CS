using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>入力値を実行せず、表示名と設定・定数・未確定入力をノード単位で検索します。</summary>
public sealed class EditorNodeSearchTests
{
    /// <summary>同名ノードをすべて検索し、改名・解除・Undoと複製に追従します。</summary>
    [Fact]
    public void DuplicateLogicalNamesRemainSearchableThroughEditsAndCopies()
    {
        NodeDefinition first = new("one", "custom.value", JsonSerializer.SerializeToElement(new { }), LogicalName: "音声の確認");
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition document = new("wf", "Workflow", [first, first with { Id = "two" }], []);
        model.Import(document);
        NodeDescriptor[] catalog = [Descriptor(first.Type, "Value")];
        UiText text = new(new(new BrowserTestRuntime()));
        EditorNodeSearch search = new();
        Assert.Equal(["one", "two"], search.Find(model.Draft, catalog, text, "音声"));
        Assert.Equal(["one", "two"], search.Find(model.Draft, catalog, text, "Value"));
        model.ChangeNode("one", node => node with { LogicalName = "送信前" });
        Assert.Equal(["two"], search.Find(model.Draft, catalog, text, "音声"));
        Assert.Equal(["one"], search.Find(model.Draft, catalog, text, "送信前"));
        model.ChangeNode("one", node => node with { LogicalName = null });
        Assert.Empty(search.Find(model.Draft, catalog, text, "送信前"));
        Assert.Equal("Value", text.NodeName(model.Draft.Document.Nodes[0], catalog[0]));
        model.Undo();
        Assert.Equal(["one"], search.Find(model.Draft, catalog, text, "送信前"));
        WorkflowDefinition copy = GraphClipboard.Clone(model.Draft.Document, new(100, 100));
        Assert.Equal(["送信前", "音声の確認"], copy.Nodes.Select(node => node.LogicalName));
        Assert.DoesNotContain(copy.Nodes, node => document.Nodes.Any(original => string.Equals(original.Id, node.Id, StringComparison.Ordinal)));
    }

    /// <summary>同じノード内の複数一致は一件とし、数値精度と未確定入力を保持します。</summary>
    [Fact]
    public void SearchIncludesNamesSettingsInputsAndInvalidText()
    {
        NodeDefinition value = new("a", "custom.value", JsonSerializer.SerializeToElement(new { text = "Needle needle", nested = new { name = "inside" } }),
            new Dictionary<string, NodeValue> { ["number"] = NodeValue.FromObject(1234567890123456789L) });
        NodeDefinition binary = new("b", "custom.binary", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue> { ["binary"] = NodeValue.FromBytes(Encoding.UTF8.GetBytes("needle")) });
        WorkflowDefinition document = new("flow", "flow", [value, binary], []);
        EditorDraft draft = new(document, document, null, document, null, null, true, [], InputDrafts: new Dictionary<string, string> { ["b/inputs/value"] = "unfinished input" });
        NodeDescriptor[] catalog = [Descriptor(value.Type, "Value"), Descriptor(binary.Type, "Binary")];
        UiText text = new(new(new BrowserTestRuntime()));
        EditorNodeSearch search = new();
        Assert.Equal(["a"], search.Find(draft, catalog, text, "NEEDLE"));
        Assert.Equal(["a"], search.Find(draft, catalog, text, "value"));
        Assert.Equal(["a"], search.Find(draft, catalog, text, "3456789012345"));
        Assert.Equal(["a"], search.Find(draft, catalog, text, "inside"));
        Assert.Equal(["b"], search.Find(draft, catalog, text, "unfinished"));
        Assert.Empty(search.Find(draft, catalog, text, " "));
        Assert.Equal(2, search.Find(draft, catalog, text, "custom").Count);
    }

    /// <summary>編集や別WFを開いた後に、前の本文やノードIDを返しません。</summary>
    [Fact]
    public void ReplacedDocumentInvalidatesSearchEntries()
    {
        NodeDefinition node = new("first", "custom.value", JsonSerializer.SerializeToElement(new { text = "old" }));
        WorkflowDefinition document = new("one", "one", [node], []);
        EditorDraft draft = new(document, document, null, document, null, null, true, []);
        NodeDescriptor[] catalog = [Descriptor(node.Type, "Value")];
        UiText text = new(new(new BrowserTestRuntime()));
        EditorNodeSearch search = new();
        Assert.Equal(["first"], search.Find(draft, catalog, text, "old"));
        draft = draft with { Document = document with { Nodes = [node with { Settings = JsonSerializer.SerializeToElement(new { text = "new" }) }] } };
        Assert.Empty(search.Find(draft, catalog, text, "old"));
        Assert.Equal(["first"], search.Find(draft, catalog, text, "new"));
        draft = draft with { Document = document with { Id = "two", Nodes = [] } };
        Assert.Empty(search.Find(draft, catalog, text, "value"));
    }

    /// <summary>名前解決に必要な契約だけを作ります。</summary>
    /// <returns>副作用のないノード種別です。</returns>
    private static NodeDescriptor Descriptor(string type, string name) => new(type, name, "Values", "Description", ENodeKind.Action, EExecutionLocation.Independent, [], [], [], JsonSerializer.SerializeToElement(new { }));
}
