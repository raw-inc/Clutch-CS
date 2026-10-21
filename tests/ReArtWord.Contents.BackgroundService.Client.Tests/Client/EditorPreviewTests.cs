using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>Peer別プレビューの照会と、分割取得の更新境界を検証します。</summary>
public sealed class EditorPreviewTests
{
    /// <summary>同じCoreの一時欠測は前回表示を維持し、再起動や明示的な空応答では解除します。</summary>
    /// <returns>以前の実体の値を新しい実体へ流用しない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryFailureKeepsOnlyTheSamePeerInstanceAsync(bool restarted)
    {
        int phase = 0;
        NodePreviewSnapshot snapshot = new(Guid.NewGuid(), "flow", "revision", "preview", "a", DateTimeOffset.UtcNow, "text/plain", 3);
        using PreviewHandler handler = new((_, _) => phase switch
        {
            0 => new[] { snapshot },
            1 => throw new HttpRequestException("Temporarily unavailable"),
            _ => Array.Empty<NodePreviewSnapshot>(),
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost") };
        EditorTestClock clock = new();
        ManagementApi api = new(client, new(clock)) { SelectedClusterId = "cluster" };
        EditorPreviews previews = new(api);
        WorkflowDefinition document = new("flow", "flow", [new("preview", "control.progress", JsonSerializer.SerializeToElement(new { }))], []);
        PeerInfo peer = Peer("a");
        await previews.RefreshAsync("cluster", document, [peer]).ConfigureAwait(true);
        phase = 1;
        clock.Advance(TimeSpan.FromSeconds(4));
        if (restarted)
        {
            peer = peer with { InstanceId = Guid.NewGuid() };
        }
        await previews.RefreshAsync("cluster", document, [peer]).ConfigureAwait(true);
        Assert.True(previews.Unavailable);
        Assert.Equal(restarted ? 0
                               : 1, previews.Find("cluster", "flow", "preview").Count);
        phase = 2;
        clock.Advance(TimeSpan.FromSeconds(4));
        await previews.RefreshAsync("cluster", document, [peer]).ConfigureAwait(true);
        Assert.False(previews.Unavailable);
        Assert.Empty(previews.Find("cluster", "flow", "preview"));
    }

    /// <summary>同じノードIDがある別WFを開いても、先に始めた照会を後から適用しません。</summary>
    /// <returns>遅いメタデータ応答の破棄です。</returns>
    [Fact]
    public async Task DelayedMetadataCannotReplaceAnotherWorkflowAsync()
    {
        TaskCompletionSource<HttpResponseMessage> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NodePreviewSnapshot snapshot = new(Guid.NewGuid(), "second", "revision", "preview", "a", DateTimeOffset.UtcNow, "text/plain", 3);
        using ClientHttpHandler handler = new(async (request, cancellationToken) =>
        {
            NodePreviewQuery query = (await request.Content!.ReadFromJsonAsync<NodePreviewQuery>(ContractJson.Options, cancellationToken).ConfigureAwait(false))!;
            string initial = "first";
            if (string.Equals(query.WorkflowId, initial, StringComparison.Ordinal))
            {
                return await first.Task.ConfigureAwait(false);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { snapshot }, options: ContractJson.Options) };
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost") };
        ManagementApi api = new(client) { SelectedClusterId = "cluster" };
        EditorPreviews previews = new(api);
        WorkflowDefinition document = new("first", "first", [new("preview", "control.preview", JsonSerializer.SerializeToElement(new { }))], []);
        PeerInfo peer = Peer("a");
        Task pending = previews.RefreshAsync("cluster", document, [peer]);
        await previews.RefreshAsync("cluster", document with { Id = "second" }, [peer]).ConfigureAwait(true);
        using HttpResponseMessage delayed = new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { snapshot with { WorkflowId = "first" } }, options: ContractJson.Options) };
        first.SetResult(delayed);
        await pending.ConfigureAwait(true);
        Assert.Equal(snapshot.Id, Assert.Single(previews.Find("cluster", "second", "preview")).Id);
        Assert.Empty(previews.Find("cluster", "first", "preview"));
    }

    /// <summary>明示ノードだけを各Peerへ照会し、クラスタ切替後に旧結果を表示しません。</summary>
    /// <returns>HTTP要求と表示対象の確認です。</returns>
    [Fact]
    public async Task MetadataKeepsPeerAndClusterIdentity()
    {
        using PreviewHandler handler = new((request, _) =>
        {
            string peer = Assert.Single(request.Headers.GetValues("X-Contents-Peer"));
            return new NodePreviewSnapshot[] { new(Guid.NewGuid(), "flow", "revision", "preview", peer, DateTimeOffset.UtcNow, "image/png", 100) };
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost") };
        ManagementApi api = new(client) { SelectedClusterId = "cluster" };
        EditorPreviews previews = new(api);
        WorkflowDefinition document = new("flow", "flow", [new("preview", "control.preview", JsonSerializer.SerializeToElement(new { }))], []);
        PeerInfo[] peers = [Peer("a"), Peer("b")];
        await previews.RefreshAsync("cluster", document, peers).ConfigureAwait(true);
        Assert.Equal(2, previews.Find("cluster", "flow", "preview").Count);
        Assert.Empty(previews.Find("other", "flow", "preview"));
        Assert.False(previews.Unavailable);
        Assert.Equal(2, handler.Calls);
        await previews.RefreshAsync("cluster", document, peers).ConfigureAwait(true);
        Assert.Equal(2, handler.Calls);
        api.SelectedClusterId = "other";
        await previews.RefreshAsync("other", document, peers).ConfigureAwait(true);
        Assert.Empty(previews.Find("cluster", "flow", "preview"));
        Assert.Equal(2, previews.Find("other", "flow", "preview").Count);
    }

    /// <summary>取得IDまたは位置が変わる応答を混ぜず、本文の長さも確認します。</summary>
    /// <returns>分割本文と不正応答の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChunksCannotMixDifferentCaptures(bool changed)
    {
        NodePreviewSnapshot snapshot = new(Guid.NewGuid(), "flow", "revision", "preview", "a", DateTimeOffset.UtcNow, "image/png", 40000);
        using PreviewHandler handler = new((_, body) =>
        {
            NodePreviewChunkRequest query = body.Deserialize<NodePreviewChunkRequest>(ContractJson.Options)!;
            Guid id = changed ? Guid.NewGuid()
                              : snapshot.Id;
            return new NodePreviewChunk(id, query.Offset, Enumerable.Repeat((byte)42, query.Length).ToArray());
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost") };
        EditorPreviews previews = new(new(client));
        if (changed)
        {
            await Assert.ThrowsAsync<IOException>(() => previews.ReadAsync("cluster", snapshot)).ConfigureAwait(true);
        }
        else
        {
            byte[] bytes = await previews.ReadAsync("cluster", snapshot).ConfigureAwait(true);
            Assert.Equal(40000, bytes.Length);
            Assert.All(bytes, value => Assert.Equal(42, value));
            Assert.Equal(2, handler.Calls);
        }
    }

    /// <summary>実体の異なる通常参加Peerを作ります。</summary>
    /// <returns>取得先のメタデータです。</returns>
    private static PeerInfo Peer(string id) => new(id, Guid.NewGuid(), id, "127.0.0.1", 12345, EPeerStatus.Ready, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>製品と同じJSON契約でHTTP応答を返します。</summary>
    private sealed class PreviewHandler(Func<HttpRequestMessage, JsonElement, object> respond) : HttpMessageHandler
    {
        internal int Calls
        {
            get; private set;
        }

        /// <summary>要求を読み取り、応答内容をシリアライズします。</summary>
        /// <returns>一回分の管理応答です。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            JsonElement body = await request.Content!.ReadFromJsonAsync<JsonElement>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(respond(request, body), options: ContractJson.Options) };
        }
    }
}
