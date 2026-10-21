using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>初回の共同編集照会を利用者の変更と誤認しません。</summary>
public sealed class InitialDraftMergeTests
{
    /// <summary>変更のない比較元でも、編集値の識別子重複を省略せず拒否します。</summary>
    [Fact]
    public void EchoFastPathStillRejectsDuplicateNodeIdentities()
    {
        WorkflowDefinition original = new("flow", "initial", [], []);
        NodeDefinition node = new("duplicate", "control.pass", JsonSerializer.SerializeToElement(new { }));
        WorkflowMergeResult result = EditorDraftResolution.Merge(original, original, original with { Nodes = [node, node] });
        Assert.Null(result.Candidate);
        Assert.Equal("merge.identityCollision", Assert.Single(result.Validation).Code);
    }

    /// <summary>自分の変更のACKでは後から加えた編集とUndoを保ち、表示中の定義を作り直しません。</summary>
    /// <returns>遅れて返る共有ACKを適用した後の編集履歴です。</returns>
    [Fact]
    public async Task EchoedDraftPreservesLaterChangesAndUndoAsync()
    {
        WorkflowDefinition saved = new("flow", "initial", [], []);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        string revision = ContractJson.Hash(saved);
        await model.LoadAsync(new(saved, revision, DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        model.Change(document => document with { Name = "sent" });
        WorkflowDefinition sent = model.Draft.Document;
        model.Change(document => document with { Name = "later" });
        WorkflowDefinition displayed = model.Draft.Document;
        model.Accept(new(saved.Id, revision, ContractJson.Hash(sent), ContractJson.Copy(sent), [], [], DateTimeOffset.UtcNow), sent);
        Assert.Same(displayed, model.Draft.Document);
        Assert.Equal("later", model.Draft.Document.Name);
        Assert.Empty(model.Draft.Conflicts);
        Assert.True(model.Draft.Dirty);
        model.Undo();
        Assert.Equal("sent", model.Draft.Document.Name);
        model.Undo();
        Assert.Equal("initial", model.Draft.Document.Name);
    }

    /// <summary>識別子順ではない保存定義でも初回Acceptはcleanを維持します。</summary>
    /// <returns>保存状態と実変更の確認です。</returns>
    [Fact]
    public async Task InitialAcceptPreservesOrderAndRealChangesRemainDirtyAsync()
    {
        JsonElement settings = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"integer\",\"text\":\"42\"}");
        WorkflowDefinition saved = new("review", "review", [new("number", "value.constant", settings), new("sum", "math.add", settings), new("bytes", "data.binary.serialize", settings)], []);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        string revision = ContractJson.Hash(saved);
        await model.LoadAsync(new(saved, revision, DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        WorkflowDefinition reordered = saved with { Nodes = saved.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray() };
        string sessionVersion = ContractJson.Hash(reordered);
        model.Accept(new(saved.Id, revision, sessionVersion, reordered, [], [], DateTimeOffset.UtcNow), saved);
        Assert.False(model.Draft.Dirty);
        Assert.False(model.CanUndo);
        Assert.Equal(["number", "sum", "bytes"], model.Draft.Document.Nodes.Select(node => node.Id));
        Assert.Equal(revision, ContractJson.Hash(model.Draft.Document));
        Assert.Equal(sessionVersion, ContractJson.Hash(model.Draft.Original));
        Assert.True(WorkflowDraftOrder.Same(model.Draft.Document, model.Draft.Original));
        model.Change(document => document with { Enabled = true });
        Assert.True(model.Draft.Dirty);
        Assert.True(model.CanUndo);
        Assert.True(model.Draft.Document.Enabled);
    }
}
