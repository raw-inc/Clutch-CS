using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>共同編集の確認応答をまたいでも、PC選択の複数接続とUndoを維持します。</summary>
public sealed class PcEditorTests
{
    /// <summary>PC選択をCommentで中継しても表示型、複数実行先、置換拒否とUndoを維持します。</summary>
    /// <returns>実編集モデルでの接続・表示の確認です。</returns>
    [Theory]
    [InlineData("execution.pc.select")]
    [InlineData("execution.pc.lowest")]
    public async Task CommentsPreservePcSelectionAndRejectIncompatibleReplacementAsync(string selectionType)
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        NodeDefinition selection = new("pc", selectionType, settings);
        NodeDefinition comment = new("comment", "value.comment", settings);
        NodeDefinition after = comment with { Id = "after" };
        NodeDefinition action = new("action", "test.action", settings);
        NodeDefinition text = new("text", "value.constant", JsonSerializer.SerializeToElement(new { type = "string", text = "host-a" }));
        NodeDescriptor[] catalog = [
            new(selectionType, "PC", "Execution", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value", EPortValueKind.PcSelection)], ["success"], settings, CanEvaluateStatically: true),
            new(comment.Type, "Comment", "Values", "", ENodeKind.Action, EExecutionLocation.Independent, [new("value"), new("displayName", EPortValueKind.Json)], [new("value")], ["success"], settings, CanEvaluateStatically: true),
            new(action.Type, "Action", "Control", "", ENodeKind.Action, EExecutionLocation.Independent, [new(PcSelectionPorts.Execution, EPortValueKind.PcSelection)], [new("value")], ["success"], settings),
            new(text.Type, "Text", "Values", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value")], ["success"], settings, CanEvaluateStatically: true),
        ];
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(new("pc-comments", "PC comments", [selection, comment, after, action, text], []), "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        EditorPresentationCache cache = new();
        Assert.Null(cache.Graph(model.Draft.Document, catalog).Port(comment.Id, "value", true).InferredType);
        Assert.True(NodeInputEditing.Connect(model, new("pc", "value", "comment", "value"), catalog));
        Assert.True(NodeInputEditing.Connect(model, new("comment", "value", "after", "value"), catalog));
        foreach (string id in new[] { "comment", "after" })
        {
            GraphTypeInference graph = cache.Graph(model.Draft.Document, catalog);
            Assert.Equal("pc-selection", graph.Port(id, "value", true).Key);
            Assert.Equal("pc-selection", graph.Port(id, "value", false).Key);
            Assert.Equal("pc-selection", graph.Port(id, "value", true).InferredType);
            Assert.Equal("pc-selection", graph.Wire(new(id, "value", "action", PcSelectionPorts.Execution)).Key);
        }
        Assert.True(NodeInputEditing.Connect(model, new("after", "value", "action", PcSelectionPorts.Execution), catalog));
        Assert.True(NodeInputEditing.Connect(model, new("pc", "value", "action", PcSelectionPorts.Execution), catalog));
        Assert.Equal(2, model.Draft.Document.Wires.Count(wire => string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal)));
        WorkflowDefinition connected = model.Draft.Document;
        Assert.False(NodeInputEditing.Connect(model, new("text", "value", "comment", "value"), catalog));
        Assert.Same(connected, model.Draft.Document);
        Assert.False(NodeInputEditing.Connect(model, new("pc", "value", "comment", "displayName"), catalog));
        model.Undo();
        Assert.Single(model.Draft.Document.Wires, wire => string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal));
    }

    /// <summary>内容が変わらない応答で接続操作を取り消さず、実変更があれば古い操作を拒否します。</summary>
    /// <returns>複数接続、Undo、共同編集の変更境界の確認。</returns>
    [Fact]
    public async Task AcknowledgementDoesNotCancelPcConnectionButRemoteChangesDoAsync()
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { mode = "all" });
        NodeDefinition first = new("first", PcSelectionPorts.SelectNode, settings);
        NodeDefinition second = first with { Id = "second" };
        NodeDefinition action = new("action", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 1 }));
        WireDefinition original = new(first.Id, "value", action.Id, PcSelectionPorts.Execution);
        WorkflowDefinition definition = new("pc-test", "PC test", [first, second, action], [original]);
        NodeDescriptor selection = new(first.Type, "PCs", "Execution", "PCs", ENodeKind.Action, EExecutionLocation.Independent, [],
            [new("value", EPortValueKind.PcSelection)], ["success"], settings, CanEvaluateStatically: true);
        NodeDescriptor target = new(action.Type, "Delay", "Control", "Delay", ENodeKind.Action, EExecutionLocation.Independent,
            [new(PcSelectionPorts.Execution, EPortValueKind.PcSelection)], [new("value")], ["success"], settings);
        NodeDescriptor[] catalog = [selection, target];
        EditorDocument model = new(new BrowserStorage(new BrowserTestRuntime()));
        string revision = ContractJson.Hash(definition);
        await model.LoadAsync(new(definition, revision, DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        WorkflowDefinition captured = model.Draft.Document;
        DraftView confirmed = new(definition.Id, revision, "session-1", ContractJson.Copy(definition), [], [], DateTimeOffset.UtcNow);
        model.Accept(confirmed, definition);
        Assert.Same(captured, model.Draft.Document);
        Assert.True(GraphDeletion.CanNode(model, action.Id));
        WireDefinition added = new(second.Id, "value", action.Id, PcSelectionPorts.Execution);
        Assert.True(NodeInputEditing.Connect(model, added, catalog, captured));
        Assert.Equal(2, model.Draft.Document.Wires.Count);
        Assert.Contains(original, model.Draft.Document.Wires);
        Assert.True(model.CanUndo);
        model.Undo();
        Assert.Equal(original, Assert.Single(model.Draft.Document.Wires));
        WorkflowDefinition beforeRemote = model.Draft.Document;
        model.Accept(confirmed with { SessionVersion = "session-2", Definition = definition with { Name = "Remote change" } }, definition);
        Assert.Equal("Remote change", model.Draft.Document.Name);
        Assert.False(NodeInputEditing.Connect(model, added, catalog, beforeRemote));
        Assert.Equal(original, Assert.Single(model.Draft.Document.Wires));
        WorkflowDefinition locked = model.Draft.Document;
        model.Locks.Update([new SnapshotEditLock(Guid.NewGuid(), definition.Id, [action.Id])]);
        model.Accept(confirmed with { Definition = ContractJson.Copy(locked) }, locked);
        Assert.Same(locked, model.Draft.Document);
        Assert.False(GraphDeletion.CanNode(model, action.Id));
        Assert.False(NodeInputEditing.Connect(model, added, catalog, locked));
    }
}
