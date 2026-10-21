using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Connectivity;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>更新待ちだけを操作可能にし、未確認をReadyと表示しません。</summary>
public sealed class PwaStatusTests
{
    /// <summary>実コンポーネントのリンクと状態表示を確認します。</summary>
    /// <returns>折り畳みなしの準備状態と、更新時だけの移動先です。</returns>
    [Theory]
    [InlineData(false, true, "Client: Ready")]
    [InlineData(true, true, "Client: Update available")]
    [InlineData(false, false, "Client: Unconfirmed")]
    public async Task OnlyAnAvailableUpdateIsLinkedAsync(bool waiting, bool ready, string label)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        services.AddSingleton<IJSRuntime>(new StatusRuntime(waiting, ready));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent result = await renderer.RenderComponentAsync<PwaStatus>().ConfigureAwait(true);
            return result.ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Contains(label, html, StringComparison.Ordinal);
        Assert.Contains("client-led", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<details", html, StringComparison.Ordinal);
        if (waiting)
        {
            Assert.Contains("href=\"/ui-update.html\"", html, StringComparison.Ordinal);
            Assert.Contains("target=\"_top\"", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("href=", html, StringComparison.Ordinal);
        }
    }

    /// <summary>ブラウザーが観測したworker状態だけを返します。</summary>
    private sealed class StatusRuntime(bool waiting, bool ready) : IJSRuntime
    {
        /// <summary>取消しを持たないInteropを同じ観測へ接続します。</summary>
        /// <returns>準備状態です。</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);

        /// <summary>公開Interopの観測値を実コンポーネントへ渡します。</summary>
        /// <returns>型付きのPWA状態です。</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("contentsBrowser.pwaStatus", identifier);
            JsonElement state = JsonSerializer.SerializeToElement(new { supported = true, registered = true, controlled = true, waiting, ready, version = "test", error = (string?)null });
            return ValueTask.FromResult(state.Deserialize<TValue>(ContractJson.Options)!);
        }
    }
}
