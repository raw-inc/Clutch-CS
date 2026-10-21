using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Updates;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>進行中と終了後の結果不明を、実際の画面描画で区別します。</summary>
public sealed class FleetUpdateRenderingTests
{
    /// <summary>実行中はRunningと件数を、終了後はUnknownと再照会を表示します。</summary>
    /// <returns>診断を含むRazor描画の確認です。</returns>
    [Theory]
    [InlineData(false, "Running")]
    [InlineData(true, "Unknown")]
    public async Task RunningAndUnconfirmedCompletionHaveDifferentLabelsAsync(bool completed, string label)
    {
        FleetUpdateTarget target = new("cluster", new("peer", Guid.NewGuid(), "host", "127.0.0.1", 47740, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow),
            new("loopback", "127.0.0.1", "127.255.255.255", 47740));
        FleetUpdateView view = new(Guid.NewGuid(), "test-release", 3, completed, EOutcome.Unknown,
            [new(target, "Completed", [new(Guid.NewGuid(), Problem: "stage: seal.Unknown")])], DateTimeOffset.UtcNow);
        using UpdateResponses handler = new(view);
        using HttpClient client = new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
        ServiceCollection services = new();
        services.AddLogging();
        BrowserTestRuntime script = new();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton(new UiText(new(script)));
        services.AddSingleton(new ManagementApi(client));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent output = await renderer.RenderComponentAsync<FleetUpdatePanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FleetUpdatePanel.Updates)] = new[] { view },
            })).ConfigureAwait(true);
            return WebUtility.HtmlDecode(output.ToHtmlString());
        }).ConfigureAwait(true);
        Assert.Contains("test-release · " + label, html, StringComparison.Ordinal);
        Assert.Contains("1/1 peers completed", html, StringComparison.Ordinal);
        Assert.Contains("stage: seal.Unknown", html, StringComparison.Ordinal);
        Assert.Equal(completed, html.Contains("Recheck saved updater results", StringComparison.Ordinal));
    }

    /// <summary>保存済みの一つの更新状態だけを返します。</summary>
    private sealed class UpdateResponses(FleetUpdateView view) : HttpMessageHandler
    {
        /// <summary>画面の定期照会に同じ状態を返します。</summary>
        /// <returns>管理通信の代わりのJSON応答です。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/client/updates", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { view }, options: ContractJson.Options) });
        }
    }
}
