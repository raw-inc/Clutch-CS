using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Pages.Logs;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実Razorの前後移動、検索条件の確定、自動再取得との切り替えを確認します。</summary>
public sealed class LogViewerPaginationTests
{
    /// <summary>前後移動では確定済み条件を使い、新しい検索と再取得で先頭へ戻ります。</summary>
    /// <returns>ボタン状態、ページ番号、取得先と検索条件です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationKeepsSearchUntilExplicitRefreshAsync(bool allClusters)
    {
        ConcurrentQueue<(string Peer, LogQuery Query)> queries = new();
        EditorTestClock clock = new();
        DateTimeOffset searchedAt = clock.GetUtcNow();
        bool fail = false;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            object? value = null;
            switch (request.RequestUri?.AbsolutePath)
            {
                case "/client/clusters":
                    PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 10000, EPeerStatus.Ready, 0, null, searchedAt);
                    value = new[] { new DiscoveredCluster("cluster", [peer], [], searchedAt) };
                    break;
                case "/api/logs/query":
                    if (fail)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    LogQuery query = (await request.Content!.ReadFromJsonAsync<LogQuery>(ContractJson.Options, token).ConfigureAwait(false))!;
                    queries.Enqueue((Assert.Single(request.Headers.GetValues("X-Contents-Peer")), query));
                    int offset = int.Parse(query.Cursor ?? "0", CultureInfo.InvariantCulture);
                    int count = Math.Min(query.Limit, 201 - offset);
                    string? cursor = offset + count < 201 ? (offset + count).ToString(CultureInfo.InvariantCulture)
                                                          : null;
                    value = new LogQueryResult(Enumerable.Range(offset, count).Select(LogPageHistoryTests.Entry).ToArray(), cursor, true);
                    break;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) };
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http, new ServerClock(clock)) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        LogViewerAccessTests.ViewerActivator activator = new();
        using ServiceProvider provider = LogViewerAccessTests.Services(api, "http://localhost/logs", activator).BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<LogViewer>().ConfigureAwait(true);
            LogViewer viewer = activator.Viewer!;
            SetField(viewer, "allClusters", allClusters);
            SetField(viewer, "text", "original");
            await SearchAsync(viewer, 0).ConfigureAwait(true);
            Assert.Contains("Page 1", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Matches("<button[^>]*disabled[^>]*>Previous page</button>", output.ToHtmlString());
            Assert.All(queries, item => Assert.Equal(searchedAt, item.Query.Until));

            SetField(viewer, "text", "edited");
            api.SelectedPeerId = "other-peer";
            clock.Advance(TimeSpan.FromSeconds(1));
            await SearchAsync(viewer, 1).ConfigureAwait(true);
            Assert.Contains("Page 2", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("entry-100", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.DoesNotContain("entry-000", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.All(queries, item =>
            {
                Assert.Equal("peer", item.Peer);
                Assert.Equal("original", item.Query.Text);
                Assert.Equal(searchedAt, item.Query.Until);
            });
            int calls = queries.Count;
            await SearchAsync(viewer, -1).ConfigureAwait(true);
            Assert.Equal(calls, queries.Count);
            Assert.Contains("Page 1", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("entry-000", output.ToHtmlString(), StringComparison.Ordinal);
            await SearchAsync(viewer, 1).ConfigureAwait(true);
            Assert.Equal(calls, queries.Count);
            await SearchAsync(viewer, 1).ConfigureAwait(true);
            Assert.Contains("Page 3", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("entry-200", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Matches("<button[^>]*disabled[^>]*>Next page</button>", output.ToHtmlString());

            fail = true;
            await SearchAsync(viewer, 0, refresh: true).ConfigureAwait(true);
            Assert.Contains("Page 3", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("entry-200", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("Logs could not be retrieved", output.ToHtmlString(), StringComparison.Ordinal);
            await SearchAsync(viewer, -1).ConfigureAwait(true);
            Assert.Contains("Page 2", output.ToHtmlString(), StringComparison.Ordinal);
            fail = false;

            SetField(viewer, "refreshSeconds", 60);
            await SearchAsync(viewer, 0, refresh: true).ConfigureAwait(true);
            Assert.Contains("Page 1", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Equal("original", queries.Last().Query.Text);
            Assert.Equal(clock.GetUtcNow(), queries.Last().Query.Until);
            Assert.Contains("Changing pages turns auto refresh off", output.ToHtmlString(), StringComparison.Ordinal);
            await SearchAsync(viewer, 1).ConfigureAwait(true);
            Assert.DoesNotContain("Changing pages turns auto refresh off", output.ToHtmlString(), StringComparison.Ordinal);
            await SearchAsync(viewer, 0).ConfigureAwait(true);
            Assert.Contains("Page 1", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Equal("edited", queries.Last().Query.Text);
        }).ConfigureAwait(true);
    }

    /// <summary>取消を無視して遅れて届く旧検索でも、新しい検索の表示を上書きしません。</summary>
    /// <returns>新しい条件の結果と先頭ページの維持です。</returns>
    [Fact]
    public async Task OldResponseCannotReplaceNewSearchAsync()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            LogQuery query = (await request.Content!.ReadFromJsonAsync<LogQuery>(ContractJson.Options, token).ConfigureAwait(false))!;
            string oldText = "old";
            if (string.Equals(query.Text, oldText, StringComparison.Ordinal))
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new LogQueryResult([LogPageHistoryTests.Entry(0) with { Message = query.Text! }], null, true), options: ContractJson.Options),
            };
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        LogViewerAccessTests.ViewerActivator activator = new();
        using ServiceProvider provider = LogViewerAccessTests.Services(api, "http://localhost/logs", activator).BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<LogViewer>().ConfigureAwait(true);
            LogViewer viewer = activator.Viewer!;
            SetField(viewer, "allClusters", false);
            SetField(viewer, "text", "old");
            Task old = SearchAsync(viewer, 0);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                SetField(viewer, "text", "new-result");
                await SearchAsync(viewer, 0).ConfigureAwait(true);
            }
            finally
            {
                release.TrySetResult();
                await old.ConfigureAwait(true);
            }
            Assert.Contains("new-result", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.DoesNotContain(">old<", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("Page 1", output.ToHtmlString(), StringComparison.Ordinal);
        }).ConfigureAwait(true);
    }

    /// <summary>入力欄と同じ値を、次のUIイベントの前に設定します。</summary>
    private static void SetField<T>(LogViewer viewer, string name, T value)
        => typeof(LogViewer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, value);

    /// <summary>描画されたボタンと同じイベントを呼び、表示を更新します。</summary>
    /// <returns>検索または前後ページ移動の完了です。</returns>
    private static async Task SearchAsync(LogViewer viewer, int direction, bool refresh = false)
    {
        MethodInfo search = typeof(LogViewer).GetMethod("SearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)search.Invoke(viewer, [direction, refresh])!).ConfigureAwait(true);
        await viewer.SetParametersAsync(ParameterView.Empty).ConfigureAwait(true);
    }
}
