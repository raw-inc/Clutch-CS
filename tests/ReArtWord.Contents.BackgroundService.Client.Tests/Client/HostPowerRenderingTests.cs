using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>状態別アイコンと、既存のホバー・自動表示条件を実描画で確認します。</summary>
public sealed class HostPowerRenderingTests
{
    /// <summary>正常時のホバー表示と、それ以外の自動テキスト表示を維持します。</summary>
    /// <returns>描画したアイコン・色条件・状態テキストの検証です。</returns>
    [Theory(Skip = "The host status now belongs to the PC heading. This component only renders the power controls.")]
    [InlineData(EPeerStatus.Ready, false, false, "good", "ready", "Ready")]
    [InlineData(EPeerStatus.Ready, true, false, "warn", "ready", "Previous / unconfirmed")]
    [InlineData(EPeerStatus.Syncing, false, false, "warn", "ring", "Syncing")]
    [InlineData(EPeerStatus.Unavailable, false, false, "warn", null, "Unavailable")]
    [InlineData(EPeerStatus.Ready, true, true, "warn offline", "none", "Offline")]
    public async Task StatusPreservesLabelsAndUsesMatchingIconAsync(EPeerStatus status, bool stale, bool offline, string css, string? animation, string label)
    {
        string html = await RenderPowerAsync(status, stale, offline).ConfigureAwait(true);
        Assert.Matches("<button[^>]*aria-label=\"[^\"]*test-PC[^\"]*\"[^>]*data-power-trigger", html);
        Assert.DoesNotContain("test-PC: " + label, html, StringComparison.Ordinal);
        Assert.Contains("class=\"health-indicator " + css + "\"", html, StringComparison.Ordinal);
        Assert.Matches("class=\"health-label\"[^>]*>" + label + "</span>", html);
        System.Text.RegularExpressions.Match wake = System.Text.RegularExpressions.Regex.Match(html, "<button[^>]*data-power-action=\"wake\"[^>]*>Send Wake-on-LAN</button>");
        Assert.True(wake.Success);
        Assert.DoesNotContain("disabled", wake.Value, StringComparison.Ordinal);
        Assert.Equal(!offline, wake.Value.Contains("hidden", StringComparison.Ordinal));
        Assert.Contains("data-power-confirm=\"wake\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"false\"", html, StringComparison.Ordinal);
        if (status == EPeerStatus.Syncing)
        {
            Assert.Contains("activity-ring", html, StringComparison.Ordinal);
            return;
        }
        if (animation is null)
        {
            Assert.Contains("power-unknown", html, StringComparison.Ordinal);
            Assert.Contains("aria-disabled=\"true\"", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("data-power-animation=\"" + animation + "\"", html, StringComparison.Ordinal);
        }
    }

    /// <summary>電源操作の対象と確認を示し、稼働状態をボタンの中で重複表示しません。</summary>
    /// <returns>通常時とオフライン時の電源操作を確認します。</returns>
    [Theory]
    [InlineData(EPeerStatus.Ready, false)]
    [InlineData(EPeerStatus.Unavailable, true)]
    public async Task PowerActionsShowTheirTargetAndKeepConfirmationAsync(EPeerStatus status, bool offline)
    {
        string html = await RenderPowerAsync(status, offline, offline).ConfigureAwait(true);
        Assert.DoesNotContain("health-indicator", html, StringComparison.Ordinal);
        Assert.Matches("<button[^>]*aria-label=\"[^\"]*test-PC[^\"]*\"[^>]*data-power-trigger", html);
        Assert.Matches("<div data-power-confirmation hidden", html);
        Assert.Contains(offline ? "data-power-confirm=\"wake\""
                                : "data-power-confirm=\"restart\"", html, StringComparison.Ordinal);
    }

    /// <summary>対象実体の状態を指定して電源操作を描画します。</summary>
    /// <returns>実際のRazor出力です。</returns>
    private static async Task<string> RenderPowerAsync(EPeerStatus status, bool stale, bool offline)
    {
        BrowserTestRuntime js = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(js);
        services.AddSingleton(new UiText(new(js)));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                [nameof(HostPowerControl.HostId)] = "test-PC",
                [nameof(HostPowerControl.Status)] = status,
                [nameof(HostPowerControl.Stale)] = stale,
                [nameof(HostPowerControl.Offline)] = offline,
            };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<HostPowerControl>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            return WebUtility.HtmlDecode(output.ToHtmlString());
        }).ConfigureAwait(true);
        return html;
    }
}
