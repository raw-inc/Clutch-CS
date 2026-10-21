using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>カードと詳細欄で共有する入力の編集境界を検証します。</summary>
public sealed class InlineEditingTests
{
    /// <summary>途中の数値を保存値へ混ぜず、修正後の精度とUndoを維持します。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task InvalidNumberRemainsDraftAndValidEditCanUndoAsync()
    {
        EditorDocument model = await CreateAsync().ConfigureAwait(true);
        NodeInputEditing.Commit(model, "n", "value", "9007199254740993", false, JsonValueKind.Number);
        NodeInputEditing.Commit(model, "n", "value", "1e", false, JsonValueKind.Number);
        Assert.Equal("1e", model.ReadInput("n/inputs/value"));
        Assert.Equal(9007199254740993L, model.Draft.Document.Nodes[0].Inputs!["value"].Json.GetInt64());
        NodeInputEditing.Commit(model, "n", "value", "42", false, JsonValueKind.Number);
        Assert.False(model.HasInvalidInputs);
        Assert.Equal(42, model.Draft.Document.Nodes[0].Inputs!["value"].Json.GetInt32());
        model.Undo();
        Assert.Equal(9007199254740993L, model.Draft.Document.Nodes[0].Inputs!["value"].Json.GetInt64());
    }

    /// <summary>接続中は定数を変更できず、配線削除後は同じ欄で編集できます。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task ConnectedInputResumesAfterWireRemovalAsync()
    {
        EditorDocument model = await CreateAsync().ConfigureAwait(true);
        model.Change(document => document with { Wires = [new("source", "value", "n", "value")] });
        NodeInputEditing.Commit(model, "n", "value", "42", false, JsonValueKind.Number);
        Assert.Null(model.Draft.Document.Nodes[0].Inputs);
        model.Change(document => document with { Wires = [] });
        NodeInputEditing.Commit(model, "n", "value", "false", false, JsonValueKind.False);
        Assert.False(model.Draft.Document.Nodes[0].Inputs!["value"].Json.GetBoolean());
        NodeInputEditing.Commit(model, "n", "text", "日本語\"そのまま", false, JsonValueKind.String);
        Assert.Equal("日本語\"そのまま", model.Draft.Document.Nodes[0].Inputs!["text"].Json.GetString());
    }

    /// <summary>復元中に届いた入力イベントは保存値と未確定草稿を変更しません。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task RestoreLockRejectsLateInputEventAsync()
    {
        EditorDocument model = await CreateAsync().ConfigureAwait(true);
        model.Locks.Update([new(Guid.NewGuid(), "flow", ["n"])]);
        NodeInputEditing.Commit(model, "n", "value", "{incomplete", false, JsonValueKind.Undefined);
        Assert.Null(model.Draft.Document.Nodes[0].Inputs);
        Assert.False(model.HasInvalidInputs);
        Assert.False(model.Draft.Dirty);
        model.Locks.Update([]);
        NodeInputEditing.Commit(model, "n", "value", "42", false, JsonValueKind.Number);
        Assert.Equal(42, model.Draft.Document.Nodes[0].Inputs!["value"].Json.GetInt32());
    }

    /// <summary>接続で置き換えた定数をUndo一回で復元し、他欄の未確定入力を残します。</summary>
    /// <returns>試験完了です。</returns>
    [Fact]
    public async Task WireReplacesConstantInOneUndoStepAsync()
    {
        EditorDocument model = await CreateAsync().ConfigureAwait(true);
        model.Change(document => document with { Nodes = [..document.Nodes, new("source", "value", JsonSerializer.SerializeToElement(new { }))] });
        NodeInputEditing.Commit(model, "n", "value", "42", false, JsonValueKind.Number);
        model.SetInput("n/inputs/value", "1e");
        model.SetInput("n/settings/other", "{");
        Assert.True(NodeInputEditing.Connect(model, new("source", "value", "n", "value")));
        Assert.False(model.Draft.Document.Nodes[0].Inputs!.ContainsKey("value"));
        Assert.Single(model.Draft.Document.Wires);
        Assert.Null(model.ReadInput("n/inputs/value"));
        Assert.Equal("{", model.ReadInput("n/settings/other"));
        model.Undo();
        Assert.Empty(model.Draft.Document.Wires);
        Assert.Equal(42, model.Draft.Document.Nodes[0].Inputs!["value"].Json.GetInt32());
    }

    /// <summary>ノードを含む独立した保存草稿を作ります。</summary>
    /// <returns>編集状態。</returns>
    private static async Task<EditorDocument> CreateAsync()
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition definition = new("flow", "base", [new("n", "value", JsonSerializer.SerializeToElement(new { }))], []);
        await model.LoadAsync(new(definition, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(false);
        return model;
    }
}
