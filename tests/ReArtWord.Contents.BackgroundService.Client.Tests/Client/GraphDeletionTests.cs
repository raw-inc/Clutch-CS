using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>メニューとキーの削除が、復元保護とUndoの経路を共有することを確認します。</summary>
public sealed class GraphDeletionTests
{
    /// <summary>主選択だけでなく全選択を削除し、接続も一回のUndoで復元します。</summary>
    /// <returns>複数選択の削除と復元の確認です。</returns>
    [Fact]
    public async Task SelectionDeletesAtomicallyAndUndoesTogetherAsync()
    {
        EditorDocument model = await CreateAsync().ConfigureAwait(true);
        model.Selection.Add(["a", "b"]);
        Assert.Equal("b", model.SelectedNode);
        Assert.True(GraphDeletion.Nodes(model, model.Selection.Nodes));
        Assert.Empty(model.Draft.Document.Nodes);
        Assert.Empty(model.Draft.Document.Wires);
        Assert.Empty(model.Selection.Nodes);
        model.Undo();
        Assert.Equal(2, model.Draft.Document.Nodes.Count);
        Assert.Single(model.Draft.Document.Wires);
    }

    /// <summary>一つでも保護対象があれば他の選択も削除しません。</summary>
    /// <returns>全選択の保護境界の確認です。</returns>
    [Fact]
    public async Task LockedSelectionPreventsPartialDeletionAsync()
    {
        EditorDocument model = await CreateAsync().ConfigureAwait(true);
        model.Selection.Add(["a", "b"]);
        model.Locks.Update([new(Guid.NewGuid(), "flow", ["b"])]);
        Assert.False(GraphDeletion.Nodes(model, model.Selection.Nodes));
        Assert.Equal(2, model.Draft.Document.Nodes.Count);
        Assert.Single(model.Draft.Document.Wires);
        Assert.Equal(2, model.Selection.Nodes.Count);
        Assert.False(model.Draft.Dirty);
    }

    /// <summary>ノードと関連線を一つの編集として削除し一回で復元できます。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task NodeAndItsWiresUndoTogetherAsync()
    {
        EditorDocument model = await CreateAsync().ConfigureAwait(true);
        model.SelectedNode = "a";
        Assert.True(GraphDeletion.Node(model, "a"));
        Assert.Single(model.Draft.Document.Nodes);
        Assert.Empty(model.Draft.Document.Wires);
        Assert.Null(model.SelectedNode);
        model.Undo();
        Assert.Equal(2, model.Draft.Document.Nodes.Count);
        Assert.Single(model.Draft.Document.Wires);
    }

    /// <summary>接続先の復元ロックも削除前に尊重します。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task LockedEndpointPreventsNodeAndWireDeletionAsync()
    {
        EditorDocument model = await CreateAsync().ConfigureAwait(true);
        model.Locks.Update([new(Guid.NewGuid(), "flow", ["b"])]);
        Assert.False(GraphDeletion.CanNode(model, "a"));
        Assert.False(GraphDeletion.Node(model, "a"));
        Assert.False(GraphDeletion.CanWire(model, 0));
        Assert.False(GraphDeletion.Wire(model, 0));
        Assert.Equal(2, model.Draft.Document.Nodes.Count);
        Assert.Single(model.Draft.Document.Wires);
        Assert.False(model.Draft.Dirty);
        model.Locks.Update([]);
        Assert.True(GraphDeletion.Wire(model, 0));
        Assert.Equal(2, model.Draft.Document.Nodes.Count);
        model.Undo();
        Assert.Single(model.Draft.Document.Wires);
        Assert.False(GraphDeletion.Wire(model, -1));
        Assert.False(GraphDeletion.Node(model, "missing"));
    }

    /// <summary>外部操作をしない独立した編集状態です。</summary>
    /// <returns>二ノードと一配線のモデルです。</returns>
    private static async Task<EditorDocument> CreateAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        JsonElement settings = JsonSerializer.Deserialize<JsonElement>("{}");
        WorkflowDefinition definition = new("flow", "test", [new("a", "control.pass", settings), new("b", "control.pass", settings)], [new("a", "value", "b", "value")]);
        await model.LoadAsync(new(definition, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(false);
        return model;
    }
}
