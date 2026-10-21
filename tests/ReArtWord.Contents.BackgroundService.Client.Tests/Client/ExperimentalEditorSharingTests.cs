using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>入力フォーカスと実保存を分けて、共有草稿の受信を確認します。</summary>
public sealed class ExperimentalEditorSharingTests
{
    /// <summary>入力中でも他ノードを受信し、当該ノードの変更だけは入力終了まで待ちます。</summary>
    /// <returns>保存前の共有と入力保護の両立です。</returns>
    [Fact]
    public async Task FocusedInputDoesNotBlockUnrelatedDraftChangesAsync()
    {
        NodeDefinition first = new("a", "control.pass", JsonSerializer.SerializeToElement(new { }));
        NodeDefinition second = first with { Id = "b" };
        WorkflowDefinition saved = new("flow", "name", [first, second], []);
        WorkflowDefinition remote = saved with { Nodes = [first, second with { Position = new(40, 50) }] };
        DraftView view = new(saved.Id, ContractJson.Hash(saved), ContractJson.Hash(remote), remote, [], [], DateTimeOffset.UtcNow);
        using ClientHttpHandler handler = new((request, _) =>
        {
            bool draft = request.RequestUri!.AbsolutePath.EndsWith("/draft", StringComparison.Ordinal);
            if (draft)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(view, options: ContractJson.Options) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<PresenceView>(), options: ContractJson.Options) });
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(saved, ContractJson.Hash(saved), DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        model.SelectedNode = "a";
        EditorCollaboration collaboration = new(model, new(client)) { AllowUnfocusedUpdates = true };
        await collaboration.SynchronizeAsync(false, "host").ConfigureAwait(true);
        Assert.Equal(new NodePosition(40, 50), model.Draft.Document.Nodes[1].Position);
        Assert.True(model.Draft.Dirty);
        WorkflowDefinition later = remote with { Nodes = [first with { Position = new(10, 20) }, remote.Nodes[1]] };
        view = view with { Definition = later, SessionVersion = ContractJson.Hash(later) };
        await collaboration.SynchronizeAsync(false, "host").ConfigureAwait(true);
        Assert.Null(model.Draft.Document.Nodes[0].Position);
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        Assert.Equal(new NodePosition(10, 20), model.Draft.Document.Nodes[0].Position);
        view = view with { BaseRevision = view.SessionVersion };
        await collaboration.SynchronizeAsync(true, "host").ConfigureAwait(true);
        Assert.False(model.Draft.Dirty);
        Assert.Equal(view.SessionVersion, model.Draft.BaseRevision);
    }
}
