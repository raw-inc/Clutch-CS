using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>通常の操作時共有と、追加の移動中共有を通信境界で区別します。</summary>
public sealed class EditorPresenceSharingTests
{
    /// <summary>既定のhoverでは送らず、移動中モードでも停止後は高頻度POSTを行いません。</summary>
    /// <returns>実際に送った座標と選択ノードの確認です。</returns>
    [Fact]
    public async Task PointerModesPublishInteractionsAndStopSendingWhileIdleAsync()
    {
        EditorTestClock clock = new();
        List<PresenceUpdate> sent = [];
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                sent.Add(JsonSerializer.Deserialize<PresenceUpdate>(await request.Content!.ReadAsStringAsync(token).ConfigureAwait(false), ContractJson.Options)!);
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<PresenceView>(), options: ContractJson.Options) };
        });
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        EditorDocument model = new(new(new BrowserTestRuntime()));
        model.Import(new("flow", "name", [], []));
        model.SelectedNode = "node-a";
        EditorPresence presence = new(model, new(client, new(clock)));
        presence.SetPointer(new(10, 20), null, null, true);
        await presence.RefreshAsync("host").ConfigureAwait(true);
        Assert.Equal(10, Assert.Single(sent).CursorX);
        presence.SetPointer(new(30, 40), null, null, false);
        await presence.RefreshAsync("host").ConfigureAwait(true);
        Assert.Single(sent);
        presence.Realtime = true;
        presence.SetPointer(new(30, 40), null, null, false);
        await presence.RefreshAsync("host").ConfigureAwait(true);
        Assert.Equal(2, sent.Count);
        Assert.Equal(30, sent[^1].CursorX);
        for (int pass = 0; pass < 10; pass++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await presence.RefreshAsync("host").ConfigureAwait(true);
        }
        Assert.Equal(2, sent.Count);
        model.SelectedNode = "node-b";
        await presence.RefreshAsync("host").ConfigureAwait(true);
        Assert.Equal("node-b", Assert.Single(sent[^1].SelectedNodeIds));
        presence.Realtime = false;
        presence.SetPointer(new(50, 60), null, null, false);
        await presence.RefreshAsync("host").ConfigureAwait(true);
        Assert.Equal(3, sent.Count);
        presence.Interact();
        await presence.RefreshAsync("host").ConfigureAwait(true);
        Assert.Equal(50, sent[^1].CursorX);
        clock.Advance(TimeSpan.FromSeconds(20));
        await presence.RefreshAsync("host").ConfigureAwait(true);
        Assert.Equal(5, sent.Count);
        model.New();
        await presence.RefreshAsync("host").ConfigureAwait(true);
        Assert.Null(sent[^1].CursorX);
        Assert.Empty(sent[^1].SelectedNodeIds);
    }

    /// <summary>接続先Peerが変わった同じブラウザーを重複表示しません。</summary>
    /// <returns>新しい在席確認だけを保持します。</returns>
    [Fact]
    public async Task ReconnectedBrowserHasOnePresenceAsync()
    {
        Guid ui = Guid.NewGuid();
        PresenceUpdate update = new("flow", ui, "host", "editor", ["node"], 10, 20);
        PresenceView old = new("old", Guid.NewGuid(), update, DateTimeOffset.UtcNow.AddSeconds(10));
        PresenceView current = old with { PeerId = "new", ExpiresAt = old.ExpiresAt.AddSeconds(10) };
        using ClientHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[] { old, current }, options: ContractJson.Options),
        }));
        using HttpClient client = new(handler) { BaseAddress = new("http://localhost/") };
        EditorDocument model = new(new(new BrowserTestRuntime()));
        model.Import(new("flow", "name", [], []));
        EditorPresence presence = new(model, new(client));
        await presence.RefreshAsync("host").ConfigureAwait(true);
        Assert.Equal("new", Assert.Single(presence.Items).PeerId);
    }
}
