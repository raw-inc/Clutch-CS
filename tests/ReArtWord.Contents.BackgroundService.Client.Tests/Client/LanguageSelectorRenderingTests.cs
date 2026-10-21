using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Components;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>言語専用メニューの表示と保存済み選択への追従を検証します。</summary>
public sealed class LanguageSelectorRenderingTests
{
    /// <summary>現在の言語を表示し、同じメニューで既存の二言語だけを選択できます。</summary>
    /// <returns>実際のRazor描画結果の検証です。</returns>
    [Theory]
    [InlineData("en", "EN", "English", false)]
    [InlineData("ja", "JP", "日本語", false)]
    [InlineData("ja", "JP", "日本語", true)]
    public async Task CurrentLanguageAndPersistenceProblemAreVisibleAsync(string language, string code, string label, bool failWrites)
    {
        BrowserTestRuntime js = new() { FailWrites = failWrites };
        UiText ui = new(new(js));
        await ui.ChangeAsync(language).ConfigureAwait(true);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(js);
        services.AddSingleton(ui);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<LanguageSelector>(ParameterView.Empty).ConfigureAwait(true);
            return WebUtility.HtmlDecode(output.ToHtmlString());
        }).ConfigureAwait(true);
        Assert.Contains(": " + label, html, StringComparison.Ordinal);
        Assert.Matches("aria-hidden=\"true\"[^>]*>" + code + "</span>", html);
        Assert.Contains("aria-checked=\"true\" lang=\"" + language, html, StringComparison.Ordinal);
        Assert.Equal(2, html.Split("role=\"menuitemradio\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("<dialog", html, StringComparison.Ordinal);
        Assert.Equal(failWrites, html.Contains("role=\"status\"", StringComparison.Ordinal));
    }
}
