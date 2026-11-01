using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Services;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実際の非同期描画で、取得中と空の結果・失敗を区別します。</summary>
public sealed class DashboardLoadingTests
{
    /// <summary>応答待ちはSkeletonを表示し、完了または失敗後には必ず解除します。</summary>
    /// <returns>HTTP待機とRazor描画の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InitialLoadShowsSkeletonUntilResponseAsync(bool allClusters, bool failure)
    {
        using PendingDiscovery handler = new(failure);
        using HttpClient client = new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
        ServiceCollection services = new();
        services.AddLogging();
        BrowserTestRuntime script = new();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton(new UiText(new(script)));
        services.AddSingleton(new ManagementApi(client));
        services.AddSingleton<NavigationManager>(new ClientTestNavigation());
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        Type component = typeof(PeerList);
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        if (allClusters)
        {
            component = typeof(AllClustersOverview);
            parameters.Add(nameof(AllClustersOverview.Active), true);
        }
        HtmlRootComponent output = default;
        Task loading = Task.CompletedTask;
        try
        {
            // 初回描画とその観測を同じDispatcher呼出しで行い、観測した後に応答を返します。
            string pending = await renderer.Dispatcher.InvokeAsync(() =>
            {
                output = renderer.BeginRenderingComponent(component, ParameterView.FromDictionary(parameters));
                loading = output.QuiescenceTask;
                try
                {
                    return output.ToHtmlString();
                }
                finally
                {
                    handler.Complete();
                }
            }).ConfigureAwait(true);
            Assert.Contains("aria-busy=\"true\"", pending, StringComparison.Ordinal);
            Assert.Contains("skeleton", pending, StringComparison.Ordinal);
            Assert.DoesNotContain("No peers have been discovered.", pending, StringComparison.Ordinal);
            await loading.ConfigureAwait(true);
            string completed = await renderer.Dispatcher.InvokeAsync(output.ToHtmlString).ConfigureAwait(true);
            Assert.Contains("aria-busy=\"false\"", completed, StringComparison.Ordinal);
            Assert.DoesNotContain("skeleton", completed, StringComparison.Ordinal);
            if (!allClusters)
            {
                if (failure)
                {
                    Assert.DoesNotContain("No peers have been discovered.", completed, StringComparison.Ordinal);
                    Assert.Contains("unconfirmed", completed, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    Assert.Contains("No peers have been discovered.", completed, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            handler.Complete();
            await loading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>ネットワーク応答の到着を試験から制御します。</summary>
    private sealed class PendingDiscovery(bool failure) : HttpMessageHandler
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>取得を完了させます。</summary>
        internal void Complete() => ready.TrySetResult();

        /// <summary>応答が届くまで空のクラスタ一覧を返しません。</summary>
        /// <returns>制御された照会結果です。</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            HttpStatusCode status = HttpStatusCode.OK;
            if (failure)
            {
                status = HttpStatusCode.ServiceUnavailable;
            }
            return new(status) { Content = new StringContent("[]") };
        }
    }
}
