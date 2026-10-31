using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実Razorで表示切替と履歴表を確認し、媒体やWF実行を要求しないことを検証します。</summary>
public sealed class PreviewHistoryRenderingTests
{
    /// <summary>最新表示では履歴を取得せず、切替後に最大50件を安全なテキストとして表示します。</summary>
    /// <returns>取得要求、DOM、再描画、別ノードへの切替の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToggleLoadsOnlyHistoryAndKeepsLatestMediaExplicitAsync(bool media, bool multiplePeers)
    {
        EditorTestClock clock = new();
        BrowserTestRuntime script = new();
        EditorDocument model = new(new(script));
        NodeDefinition node = new("preview", "control.preview", JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition document = new("flow", "Preview", [node], []);
        await model.LoadAsync(new(document, "saved", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        using HistoryHandler handler = new();
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost") };
        ManagementApi api = new(http, new(clock)) { SelectedClusterId = "cluster" };
        PreviewActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(model);
        services.AddSingleton(new EditorPreviews(api));
        services.AddSingleton(new EditorConcurrencyQueues(api));
        services.AddSingleton(new UiText(new(script)));
        services.AddSingleton<EditorPresentationCache>();
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            string type = "text/plain";
            if (media)
            {
                type = "image/png";
            }
            NodePreviewSnapshot latest = new(Guid.NewGuid(), "flow", "saved", "preview", "a", clock.GetUtcNow(), type, 6);
            NodePreviewSnapshot[] snapshots = [latest];
            if (multiplePeers)
            {
                snapshots = [latest, latest with { Id = Guid.NewGuid(), PeerId = "b" }];
            }
            Dictionary<string, object?> parameters = new()
            {
                ["Node"] = node, ["ClusterId"] = "cluster", ["WorkflowId"] = "flow", ["Snapshots"] = snapshots,
            };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root = await renderer.RenderComponentAsync<NodePreviewPanel>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Equal(0, handler.HistoryReads);
            int expectedChunks = media ? 0
                                       : 1;
            Assert.Equal(expectedChunks, handler.ChunkReads);
            Assert.Contains("History", root.ToHtmlString(), StringComparison.Ordinal);
            Assert.DoesNotContain("<table", root.ToHtmlString(), StringComparison.Ordinal);
            await activator.Panel!.SetHistoryAsync(true).ConfigureAwait(true);
            await activator.Panel.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            string html = root.ToHtmlString();
            Assert.Equal(snapshots.Length, handler.HistoryReads);
            Assert.Equal(expectedChunks, handler.ChunkReads);
            Assert.Equal(51, html.Split("<tr", StringSplitOptions.None).Length - 1);
            Assert.Matches("<th[^>]*>Index</th><th[^>]*>Time</th><th[^>]*>Value</th>", html);
            Assert.Contains("Latest 50 captures", html, StringComparison.Ordinal);
            Assert.Contains("2026-10-03 15:01:15.123", html, StringComparison.Ordinal);
            Assert.Contains(">15:01:15.123</time>", html, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong-workflow", html, StringComparison.Ordinal);
            Assert.DoesNotContain("row-24", html, StringComparison.Ordinal);
            Assert.Contains("image/png", html, StringComparison.Ordinal);
            await activator.Panel.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Equal(snapshots.Length, handler.HistoryReads);
            parameters["Snapshots"] = snapshots.Select(snapshot => snapshot with { Id = Guid.NewGuid() }).ToArray();
            await activator.Panel.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Equal(snapshots.Length * 2, handler.HistoryReads);
            Assert.Equal(expectedChunks, handler.ChunkReads);
            await activator.Panel.SetHistoryAsync(false).ConfigureAwait(true);
            await activator.Panel.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.DoesNotContain("<table", root.ToHtmlString(), StringComparison.Ordinal);
            Assert.Equal(snapshots.Length * 2, handler.HistoryReads);
            Assert.Equal(expectedChunks * 2, handler.ChunkReads);
            Assert.Equal(document, model.Draft.Document);
        }).ConfigureAwait(true);
    }

    /// <summary>同じPeerの並走実行も別バーとして描画し、本文要求を発生させません。</summary>
    /// <returns>実Razorの複数表示、完了後の除去、追加取得ゼロの確認です。</returns>
    [Fact]
    public async Task ProgressSessionsRenderSeparatelyWithoutChunkRequestsAsync()
    {
        EditorTestClock clock = new();
        BrowserTestRuntime script = new();
        EditorDocument model = new(new(script));
        NodeDefinition node = new("progress", "control.progress", JsonSerializer.SerializeToElement(new { }));
        using HistoryHandler handler = new();
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost") };
        ManagementApi api = new(http, new(clock)) { SelectedClusterId = "cluster" };
        PreviewActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(model);
        services.AddSingleton(new EditorPreviews(api));
        services.AddSingleton(new EditorConcurrencyQueues(api));
        services.AddSingleton(new UiText(new(script)));
        services.AddSingleton<EditorPresentationCache>();
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            DateTimeOffset now = clock.GetUtcNow();
            ProgressSession session = new(Guid.NewGuid(), "owner", Guid.NewGuid(), now);
            NodePreviewSnapshot first = new(Guid.NewGuid(), "flow", "saved", "progress", "worker", now, "text/plain", 40)
            { Session = session, Progress = new(10, 3) };
            NodePreviewSnapshot second = first with { Id = Guid.NewGuid(), Session = session with { RunId = Guid.NewGuid() }, Progress = new(100, 80) };
            Dictionary<string, object?> parameters = new()
            {
                ["Node"] = node, ["ClusterId"] = "cluster", ["WorkflowId"] = "flow", ["RuntimeOnly"] = true,
                ["Snapshots"] = new[] { first, second, first with { Id = Guid.NewGuid(), PeerId = "other", CapturedAt = now.AddSeconds(-1) } },
            };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root = await renderer.RenderComponentAsync<NodePreviewPanel>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            string html = root.ToHtmlString();
            Assert.Contains("progress-stack", html, StringComparison.Ordinal);
            Assert.Equal(2, html.Split("<progress ", StringSplitOptions.None).Length - 1);
            Assert.Contains("30%", html, StringComparison.Ordinal);
            Assert.Contains("80%", html, StringComparison.Ordinal);
            Assert.Equal(0, handler.ChunkReads);
            parameters["Snapshots"] = new[] { second };
            await activator.Panel!.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Equal(1, root.ToHtmlString().Split("<progress ", StringSplitOptions.None).Length - 1);
            Assert.Equal(0, handler.ChunkReads);
            parameters["Snapshots"] = new[] { second with { CapturedAt = now.AddMinutes(-6) } };
            await activator.Panel.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.DoesNotContain("<progress ", root.ToHtmlString(), StringComparison.Ordinal);
        }).ConfigureAwait(true);
    }

    /// <summary>履歴の概要だけを返し、想定外の実行・ファイル読取りを拒否します。</summary>
    private sealed class HistoryHandler : HttpMessageHandler
    {
        internal int HistoryReads
        {
            get; private set;
        }

        internal int ChunkReads
        {
            get; private set;
        }

        /// <summary>取得先のWFとPeerを確認し、意図的に別WFの行も混ぜます。</summary>
        /// <returns>製品契約でシリアライズした観測データです。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonElement body = await request.Content!.ReadFromJsonAsync<JsonElement>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
            string history = "/api/previews/history", chunk = "/api/previews/chunk";
            object result;
            if (string.Equals(request.RequestUri!.AbsolutePath, history, StringComparison.Ordinal))
            {
                HistoryReads++;
                NodePreviewHistoryQuery query = body.Deserialize<NodePreviewHistoryQuery>(ContractJson.Options)!;
                Assert.Equal("flow", query.WorkflowId);
                Assert.Equal("preview", query.NodeId);
                string peer = Assert.Single(request.Headers.GetValues("X-Contents-Peer"));
                DateTimeOffset time = new(2026, 10, 4, 0, 0, 0, 123, TimeSpan.FromHours(9));
                List<NodePreviewHistoryEntry> rows = Enumerable.Range(1, 75).Select(number => new NodePreviewHistoryEntry(number,
                    new(Guid.NewGuid(), "flow", "saved", "preview", peer, time.AddSeconds(number), "text/plain", 6), "row-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
                rows[^1] = rows[^1] with { Text = "<script>alert(1)</script>" };
                rows[^2] = rows[^2] with { Text = null, Snapshot = rows[^2].Snapshot with { MediaType = "image/png", Length = 1000000 } };
                rows.Add(rows[^1] with { Number = 999, Text = "wrong-workflow", Snapshot = rows[^1].Snapshot with { WorkflowId = "other" } });
                result = rows;
            }
            else
            {
                Assert.Equal(chunk, request.RequestUri.AbsolutePath);
                ChunkReads++;
                NodePreviewChunkRequest query = body.Deserialize<NodePreviewChunkRequest>(ContractJson.Options)!;
                result = new NodePreviewChunk(query.Id, query.Offset, Encoding.UTF8.GetBytes("latest"));
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: ContractJson.Options) };
        }
    }

    /// <summary>イベントを実コンポーネントへ届けるため、生成されたパネルだけを参照します。</summary>
    private sealed class PreviewActivator : IComponentActivator
    {
        internal NodePreviewPanel? Panel
        {
            get; private set;
        }

        /// <summary>通常のBlazor生成を維持します。</summary>
        /// <returns>未初期化のコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is NodePreviewPanel panel)
            {
                Panel = panel;
            }
            return component;
        }
    }
}
