using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>PWA再起動後に前回の完全保存版を編集できます。</summary>
public sealed class OfflineWorkflowTests
{
    /// <summary>cleanな最後の版を復元し、別の取得済み定義をオフラインで再選択します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CleanReloadAndOfflineReselectionUseOnlyAcquiredDefinitionsAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        await storage.WriteAsync("contents.client.cluster", "cluster", true).ConfigureAwait(true);
        bool offline = false;
        using ClientHttpHandler handler = new((request, _) =>
        {
            if (offline)
            {
                throw new HttpRequestException("offline");
            }
            string id = request.RequestUri!.Segments[^1];
            WorkflowDefinition definition = new(id, "saved-" + id, [], []);
            WorkflowView view = new(new(definition, "revision-" + id, DateTimeOffset.UnixEpoch, EActivationPolicy.Explicit), null, null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(view, ContractJson.Options), Encoding.UTF8, "application/json") });
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        await EditorWorkflowLoader.OpenAsync(model, api, storage, "a").ConfigureAwait(true);
        await EditorWorkflowLoader.OpenAsync(model, api, storage, "b").ConfigureAwait(true);
        Assert.False(model.Draft.Dirty);
        offline = true;
        EditorDocument reloaded = new(storage);
        await reloaded.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("b", reloaded.Draft.Document.Id);
        Assert.False(reloaded.Draft.Dirty);
        string source = await EditorWorkflowLoader.OpenAsync(reloaded, api, storage, "a").ConfigureAwait(true);
        Assert.Contains("offline", source, StringComparison.Ordinal);
        Assert.Equal("saved-a", reloaded.Draft.Document.Name);
        Assert.Equal("revision-a", reloaded.Draft.BaseRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EditorWorkflowLoader.OpenAsync(reloaded, api, storage, "never-read")).ConfigureAwait(true);
        Assert.Equal("a", reloaded.Draft.Document.Id);
        Assert.Null(await EditorSavedCache.ReadAsync(storage, "cluster", "other-peer", "a").ConfigureAwait(true));
        Assert.Null(await EditorSavedCache.ReadAsync(storage, "other-cluster", "peer", "a").ConfigureAwait(true));
    }

    /// <summary>同画面の新しいclean版や未確定入力を古い取得キャッシュで上書きしません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task RetainedCleanAndRawDraftRemainOwnedByTheirUiAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        await model.InitializeAsync().ConfigureAwait(true);
        WorkflowDefinition definition = new("flow", "latest-confirmed", [], []);
        await model.LoadAsync(new(definition, "latest", DateTimeOffset.UtcNow, EActivationPolicy.Explicit)).ConfigureAwait(true);
        model.SetInput("pending/raw", "{unfinished");
        await model.PersistAsync().ConfigureAwait(true);
        model.New();
        Assert.True(await model.RestoreRetainedAsync("flow").ConfigureAwait(true));
        Assert.Equal("latest", model.Draft.BaseRevision);
        Assert.Equal("{unfinished", model.ReadInput("pending/raw"));
    }
}
