using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>複数選択と貼り付けを文書・Undo・編集ロックの境界で検証します。</summary>
public sealed class GraphSelectionTests
{
    /// <summary>単一選択、トグル、範囲加算で選択の重複や主選択の欠落を作りません。</summary>
    [Fact]
    public void ToggleAndRangeSelectionPreserveSingleSelectionBehavior()
    {
        GraphSelection selection = new();
        selection.Single("a");
        selection.Toggle("b");
        Assert.Equal("b", selection.Primary);
        Assert.Equal(2, selection.Nodes.Count);
        selection.Toggle("b");
        Assert.Equal("a", selection.Primary);
        selection.Add(["a", "c", "d"]);
        Assert.Equal(3, selection.Nodes.Count);
        selection.Retain(["a", "c"]);
        Assert.Contains(selection.Primary!, selection.Nodes);
        selection.Single("c");
        Assert.Equal("c", Assert.Single(selection.Nodes));
        selection.Toggle("c");
        Assert.Empty(selection.Nodes);
        Assert.Null(selection.Primary);
    }

    /// <summary>内部線の条件・抽出を保持し、外側の線を複製せず相対位置を合わせます。</summary>
    [Fact]
    public void PasteRemapsIdsRetainsInternalWiresAndAnchorsBoundingCorner()
    {
        EditorDocument model = Model();
        WorkflowDefinition original = model.Draft.Document;
        GraphClipboard clipboard = model.Selection.Clipboard;
        clipboard.Copy(original, new HashSet<string>(["a", "b"], StringComparer.Ordinal));
        IReadOnlyList<string> ids = clipboard.Paste(model, new(800, 600));
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("a", ids);
        NodeDefinition a = model.Draft.Document.Nodes.Single(node => string.Equals(node.Id, ids[0], StringComparison.Ordinal));
        NodeDefinition b = model.Draft.Document.Nodes.Single(node => string.Equals(node.Id, ids[1], StringComparison.Ordinal));
        Assert.Equal(new NodePosition(900, 600), a.Position);
        Assert.Equal(new NodePosition(800, 800), b.Position);
        Assert.Equal(original.Nodes[0].Size, a.Size);
        Assert.True(JsonElement.DeepEquals(original.Nodes[0].Settings, a.Settings));
        Assert.Equal("constant", a.Inputs!["value"].GetString());
        WireDefinition wire = Assert.Single(model.Draft.Document.Wires, item => ids.Contains(item.FromNode, StringComparer.Ordinal));
        Assert.Equal(ids[1], wire.ToNode);
        Assert.Equal("failure", wire.When);
        Assert.Equal("/nested", wire.Path);
        Assert.Equal(ids.Order(StringComparer.Ordinal), model.Selection.Nodes.Order(StringComparer.Ordinal));
        model.Undo();
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(model.Draft.Document));
        Assert.Empty(model.Selection.Nodes);
        IReadOnlyList<string> repeated = clipboard.Paste(model, new(10, 20));
        Assert.Empty(ids.Intersect(repeated, StringComparer.Ordinal));
    }

    /// <summary>片端だけのコピーは線を持たず、元の後続編集から独立します。</summary>
    [Fact]
    public void SingleNodeClipboardIsAnIndependentSnapshot()
    {
        EditorDocument model = Model();
        model.Selection.Clipboard.Copy(model.Draft.Document, new HashSet<string>(["a"], StringComparer.Ordinal));
        model.ChangeNode("a", node => node with { Settings = JsonSerializer.SerializeToElement(new { changed = true }) });
        string id = Assert.Single(model.Selection.Clipboard.Paste(model, new(20, 30)));
        NodeDefinition pasted = model.Draft.Document.Nodes.Single(node => string.Equals(node.Id, id, StringComparison.Ordinal));
        Assert.True(pasted.Settings.TryGetProperty("original", out _));
        Assert.DoesNotContain(model.Draft.Document.Wires, wire => string.Equals(wire.FromNode, id, StringComparison.Ordinal) || string.Equals(wire.ToNode, id, StringComparison.Ordinal));
        Assert.Equal(new NodePosition(20, 30), pasted.Position);
    }

    /// <summary>文書全体がロックされているときは部分適用も履歴変更もしません。</summary>
    [Fact]
    public void PasteCannotBypassWholeDocumentLock()
    {
        EditorDocument model = Model();
        model.Selection.Clipboard.Copy(model.Draft.Document, new HashSet<string>(["a", "b"], StringComparer.Ordinal));
        WorkflowDefinition before = model.Draft.Document;
        model.Locks.Update([new(Guid.NewGuid(), before.Id, null)]);
        Assert.Empty(model.Selection.Clipboard.Paste(model, new(100, 100)));
        Assert.Same(before, model.Draft.Document);
        Assert.False(model.CanUndo);
    }

    /// <summary>左端と上端が別ノードにあるグラフを作ります。</summary>
    /// <returns>外側のノードへの線も持つ草稿です。</returns>
    private static EditorDocument Model()
    {
        EditorDocument model = new(new BrowserStorage(new BrowserTestRuntime()));
        JsonElement settings = JsonSerializer.SerializeToElement(new { original = true });
        model.Import(new("workflow", "Workflow", [
            new("a", "test", settings, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("constant") }, Position: new(200, 100), Size: new(560, 520)),
            new("b", "test", settings, Position: new(100, 300)), new("c", "test", settings, Position: new(600, 300)),
        ], [new("a", "value", "b", "value", "failure", "/nested"), new("b", "value", "c", "value")]));
        return model;
    }
}
