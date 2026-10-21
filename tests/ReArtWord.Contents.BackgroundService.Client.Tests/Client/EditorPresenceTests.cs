using System.Net;
using System.Text;
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

/// <summary>編集中の座標を定義変更と分け、切替前の通知を新画面へ持ち込みません。</summary>
public sealed class EditorPresenceTests
{
    /// <summary>ポインター・ドラッグの送信は保存版と未保存フラグを変更しません。</summary>
    /// <returns>送信内容と草稿の独立性の確認です。</returns>
    [Fact]
    public async Task PointerAndDragArePublishedWithoutEditingDefinitionAsync()
    {
        PresenceUpdate? sent = null;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                sent = JsonSerializer.Deserialize<PresenceUpdate>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options);
            }
            return Reply(Array.Empty<PresenceView>());
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost") };
        EditorDocument model = new(new(new BrowserTestRuntime()));
        WorkflowDefinition definition = new("presence", "Presence", [], []);
        await model.LoadAsync(new(definition, ContractJson.Hash(definition), DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        EditorCollaboration collaboration = new(model, new ManagementApi(client)) { RealtimePointer = true };
        collaboration.SetPointer(new(40, 60), "node", new(100, 120));
        await collaboration.SynchronizePresenceAsync("host").ConfigureAwait(true);
        Assert.NotNull(sent);
        Assert.Equal(40, sent.CursorX);
        Assert.Equal(new NodePosition(100, 120), sent.DragPosition);
        Assert.Equal("node", sent.DragNodeId);
        Assert.False(model.Draft.Dirty);
        Assert.Equal(ContractJson.Hash(definition), ContractJson.Hash(model.Draft.Document));
        model.New();
        await collaboration.SynchronizePresenceAsync("host").ConfigureAwait(true);
        Assert.Equal(model.Draft.Document.Id, sent!.WorkflowId);
        Assert.Null(sent.CursorX);
        Assert.Null(sent.DragNodeId);
        Assert.Null(sent.DragPosition);
    }

    /// <summary>別ワークフローへ切り替えた後の遅延通知は表示しません。</summary>
    /// <returns>非同期応答の世代境界の確認です。</returns>
    [Fact]
    public async Task LatePresenceCannotEnterAnotherWorkflowAsync()
    {
        TaskCompletionSource<HttpResponseMessage> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ClientHttpHandler handler = new((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return pending.Task;
            }
            return Task.FromResult(Reply(Array.Empty<PresenceView>()));
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost") };
        EditorDocument model = new(new(new BrowserTestRuntime()));
        EditorCollaboration collaboration = new(model, new ManagementApi(client));
        string oldId = model.Draft.Document.Id;
        Task waiting = collaboration.SynchronizePresenceAsync("host");
        model.New();
        PresenceView old = new("peer", Guid.NewGuid(), new(oldId, Guid.NewGuid(), "other", "Other", [], 10, 20), DateTimeOffset.UtcNow.AddMinutes(1));
        using HttpResponseMessage delivered = Reply(new[] { old });
        pending.SetResult(delivered);
        await waiting.ConfigureAwait(true);
        Assert.Empty(collaboration.Presence);
    }

    /// <summary>実クライアントと同じJSON契約で応答します。</summary>
    /// <returns>呼出先が所有して破棄するHTTP応答です。</returns>
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, ContractJson.Options), Encoding.UTF8, "application/json"),
    };
}
