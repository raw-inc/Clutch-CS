using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Layout;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Overview.Updates;
using ReArtWord.Contents.BackgroundService.Client.Pages.Logs;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>接続先がない状態でも、実レイアウトから全クラスタのログを検索できます。</summary>
public sealed class LogViewerAccessTests
{
    /// <summary>未選択・Peer選択・解除で検索先を切り替え、各検索の宛先を固定します。</summary>
    /// <returns>レイアウト、Razorの検索処理、HTTPの取得先を通した確認です。</returns>
    [Theory(Skip = "旧仕様: Peer選択時に検索を自動で選択Peerへ限定していた。既定は全クラスタへ変更し、SearchDefaultsAndExplicitFiltersAsyncで確認します。")]
    [InlineData("logs")]
    [InlineData("logs/?experimental=true")]
    public async Task UnselectedLayoutSearchesAllClustersWithoutChangingSelectionAsync(string path)
    {
        ConcurrentQueue<string> queries = new();
        int statistics = 0;
        using ClientHttpHandler handler = new((request, _) =>
        {
            object? value = null;
            string? requestPath = request.RequestUri?.AbsolutePath;
            switch (requestPath)
            {
                case "/client/branding": value = new ClientBranding(); break;
                case "/client/updates/visible": value = new FleetUpdateOverview([], []); break;
                case "/client/clusters": value = Clusters(); break;
                case "/api/logs/statistics":
                    Interlocked.Increment(ref statistics);
                    value = new LogStatistics(0, 0, 0, 0, 0, true, null);
                    break;
                case "/api/logs/query":
                    string cluster = Assert.Single(request.Headers.GetValues("X-Contents-Cluster"));
                    Assert.Equal("peer", Assert.Single(request.Headers.GetValues("X-Contents-Peer")));
                    queries.Enqueue(cluster);
                    value = new LogQueryResult([new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "ERROR", "peer", "entry-" + cluster)], null, true);
                    break;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) });
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http);
        ViewerActivator activator = new();
        ServiceCollection services = Services(api, "http://localhost/" + path, activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment body = builder =>
            {
                builder.OpenComponent<LogViewer>(0);
                builder.CloseComponent();
            };
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(MainLayout.Body)] = body });
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<MainLayout>(parameters).ConfigureAwait(true);
            string initial = output.ToHtmlString();
            Assert.Contains("log-page", initial, StringComparison.Ordinal);
            Assert.Contains("value=\"all\"", initial, StringComparison.Ordinal);
            Assert.Contains("value=\"current\" disabled", initial, StringComparison.Ordinal);
            Assert.Equal(0, statistics);
            await SearchAsync(activator.Viewer!).ConfigureAwait(true);
            Assert.Equal(["a", "b"], queries.Order(StringComparer.Ordinal));
            Assert.Contains("entry-a", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("entry-b", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Null(api.SelectedClusterId);
            Assert.Null(api.SelectedPeerId);

            api.SelectedClusterId = "a";
            api.SelectedPeerId = "peer";
            await activator.Layout!.SetParametersAsync(parameters).ConfigureAwait(true);
            queries.Clear();
            await SearchAsync(activator.Viewer!).ConfigureAwait(true);
            Assert.Equal("a", Assert.Single(queries));
            Assert.Contains("value=\"current\"", output.ToHtmlString(), StringComparison.Ordinal);

            api.SelectedPeerId = null;
            await activator.Layout.SetParametersAsync(parameters).ConfigureAwait(true);
            queries.Clear();
            await SearchAsync(activator.Viewer!).ConfigureAwait(true);
            Assert.Equal(["a", "b"], queries.Order(StringComparer.Ordinal));
            Assert.Equal("a", api.SelectedClusterId);
            Assert.Null(api.SelectedPeerId);
            Assert.Contains("value=\"current\" disabled", output.ToHtmlString(), StringComparison.Ordinal);
        }).ConfigureAwait(true);
    }

    /// <summary>接続先の有無で既定を変えず、検索範囲・非圧縮限定を明示変更できます。</summary>
    /// <returns>実Razorから送信した全クラスタ・限定形式の検索要求です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchDefaultsAndExplicitFiltersAsync(bool selected)
    {
        EditorTestClock clock = new();
        ConcurrentQueue<(string Cluster, LogQuery Query)> queries = new();
        using ClientHttpHandler handler = new(async (request, cancellationToken) =>
        {
            object? value = request.RequestUri?.AbsolutePath switch
            {
                "/client/branding" => new ClientBranding(),
                "/client/updates/visible" => new FleetUpdateOverview([], []),
                "/client/transfers/active-count" => 0,
                "/client/clusters" => Clusters(),
                "/api/logs/statistics" => new LogStatistics(0, 0, 0, 0, 0, true, null),
                _ => null
            };
            string queryPath = "/api/logs/query";
            if (string.Equals(request.RequestUri?.AbsolutePath, queryPath, StringComparison.Ordinal))
            {
                string cluster = Assert.Single(request.Headers.GetValues("X-Contents-Cluster"));
                LogQuery filter = (await request.Content!.ReadFromJsonAsync<LogQuery>(ContractJson.Options, cancellationToken).ConfigureAwait(false))!;
                queries.Enqueue((cluster, filter));
                value = new LogQueryResult([new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "ERROR", "peer", "entry-" + cluster)], null, true);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) };
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http, new(clock));
        if (selected)
        {
            api.SelectedClusterId = "a";
            api.SelectedPeerId = "peer";
        }
        ViewerActivator activator = new();
        using ServiceProvider provider = Services(api, "http://localhost/logs", activator).BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment body = builder =>
            {
                builder.OpenComponent<LogViewer>(0);
                builder.CloseComponent();
            };
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(MainLayout.Body)] = body });
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<MainLayout>(parameters).ConfigureAwait(true);
            Assert.Contains("type=\"checkbox\" checked", output.ToHtmlString(), StringComparison.Ordinal);
            await SearchAsync(activator.Viewer!).ConfigureAwait(true);
            Assert.Equal(["a", "b"], queries.Select(item => item.Cluster).Order(StringComparer.Ordinal));
            Assert.All(queries, item => Assert.True(item.Query.UncompressedOnly));

            api.SelectedClusterId = "a";
            api.SelectedPeerId = "peer";
            await activator.Layout!.SetParametersAsync(parameters).ConfigureAwait(true);
            queries.Clear();
            await SearchAsync(activator.Viewer!).ConfigureAwait(true);
            Assert.Equal(["a", "b"], queries.Select(item => item.Cluster).Order(StringComparer.Ordinal));
            queries.Clear();
            await ChangeAsync(activator.Viewer!, "ScopeChangedAsync", "current").ConfigureAwait(true);
            Assert.Equal("a", Assert.Single(queries).Cluster);
            Assert.True(Assert.Single(queries).Query.UncompressedOnly);

            queries.Clear();
            await ChangeAsync(activator.Viewer!, "UncompressedOnlyChangedAsync", false).ConfigureAwait(true);
            Assert.False(Assert.Single(queries).Query.UncompressedOnly);
            Assert.Null(Assert.Single(queries).Query.Cursor);
            queries.Clear();
            await ChangeAsync(activator.Viewer!, "RefreshChangedAsync", "5").ConfigureAwait(true);
            Assert.False(Assert.Single(queries).Query.UncompressedOnly);
            queries.Clear();
            await ChangeAsync(activator.Viewer!, "UncompressedOnlyChangedAsync", true).ConfigureAwait(true);
            Assert.True(Assert.Single(queries).Query.UncompressedOnly);

            api.SelectedPeerId = null;
            await activator.Layout!.SetParametersAsync(parameters).ConfigureAwait(true);
            queries.Clear();
            await SearchAsync(activator.Viewer!).ConfigureAwait(true);
            Assert.Equal(["a", "b"], queries.Select(item => item.Cluster).Order(StringComparer.Ordinal));
            Assert.Equal("a", api.SelectedClusterId);
            Assert.Null(api.SelectedPeerId);
        }).ConfigureAwait(true);
    }

    /// <summary>描画された入力の変更と同じコールバックを実行します。</summary>
    /// <returns>先頭ページの再検索完了です。</returns>
    private static async Task ChangeAsync(LogViewer viewer, string method, object value)
    {
        MethodInfo change = typeof(LogViewer).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)change.Invoke(viewer, [new ChangeEventArgs { Value = value }])!).ConfigureAwait(true);
    }

    /// <summary>実コンポーネントの検索イベントを呼び出し、結果を再描画します。</summary>
    /// <returns>検索と描画の完了です。</returns>
    private static async Task SearchAsync(LogViewer viewer, CancellationToken cancellationToken = default)
    {
        MethodInfo search = typeof(LogViewer).GetMethod("SearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)search.Invoke(viewer, [0, false])!).WaitAsync(cancellationToken).ConfigureAwait(true);
        await viewer.SetParametersAsync(ParameterView.Empty).ConfigureAwait(true);
    }

    /// <summary>同じPeer IDを持つ二つの観測クラスタを返します。</summary>
    /// <returns>クラスタを省略すると区別できない取得先です。</returns>
    private static DiscoveredCluster[] Clusters()
    {
        PeerInfo peer = new("peer", Guid.NewGuid(), "host", "127.0.0.1", 10000, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow);
        return [new("a", [peer], [], DateTimeOffset.UtcNow), new("b", [peer], [], DateTimeOffset.UtcNow)];
    }

    /// <summary>本番への接続を持たず、実レイアウトの依存だけを構成します。</summary>
    /// <returns>描画用サービスです。</returns>
    internal static ServiceCollection Services(ManagementApi api, string uri, ViewerActivator activator)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(new LayoutScript());
        services.AddSingleton<BrowserStorage>();
        services.AddSingleton(provider => new UiText(provider.GetRequiredService<BrowserStorage>()));
        services.AddSingleton<DisplayTimePreferences>();
        services.AddSingleton(api);
        services.AddSingleton<ConnectionState>();
        services.AddSingleton<EditorDocument>();
        services.AddSingleton<EditorTargetSelection>();
        services.AddSingleton<OperationJournalStorage>();
        services.AddSingleton<OperationJournal>();
        services.AddSingleton<FleetUpdateProgress>();
        services.AddSingleton<NavigationManager>(new ClientTestNavigation(uri));
        services.AddSingleton<IComponentActivator>(activator);
        return services;
    }

    /// <summary>イベント呼出し対象となる実レイアウトと実ページを保持します。</summary>
    internal sealed class ViewerActivator : IComponentActivator
    {
        internal MainLayout? Layout
        {
            get; private set;
        }

        internal LogViewer? Viewer
        {
            get; private set;
        }

        /// <summary>通常と同じ型を生成し、初期化はRendererへ委ねます。</summary>
        /// <returns>依存注入前のコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is MainLayout layout)
            {
                Layout = layout;
            }
            if (component is LogViewer viewer)
            {
                Viewer = viewer;
            }
            return component;
        }
    }

    /// <summary>保存領域は実テスト実装を使い、ブラウザー専用の装飾だけを省略します。</summary>
    private sealed class LayoutScript : IJSRuntime
    {
        private readonly BrowserTestRuntime storage = new();

        /// <summary>取消なしのブラウザー呼出しを転送します。</summary>
        /// <returns>試験用の値です。</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        /// <summary>レイアウトの表示操作と端末保存を分離します。</summary>
        /// <returns>ブラウザーAPIの応答です。</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            string favicon = "contentsBranding.setFavicon", status = "contentsBrowser.pwaStatus";
            if (string.Equals(identifier, favicon, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(default(TValue)!);
            }
            if (string.Equals(identifier, status, StringComparison.Ordinal))
            {
                throw new JSException("Not available in the rendering fixture");
            }
            return storage.InvokeAsync<TValue>(identifier, cancellationToken, args);
        }
    }
}
