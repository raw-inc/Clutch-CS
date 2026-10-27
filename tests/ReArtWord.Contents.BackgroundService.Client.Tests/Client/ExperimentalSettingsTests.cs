using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;
using ReArtWord.Contents.BackgroundService.Client.Services;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>試験的な機能の保存操作と、再表示・保存障害時の表示を確認します。</summary>
public sealed class ExperimentalSettingsTests
{
    /// <summary>選択を保存し、保存できた値だけを画面の再生成後へ引き継ぎます。</summary>
    /// <returns>実コンポーネントの操作と再表示の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SaveAndReopenReflectOnlyPersistedPreferenceAsync(bool original, bool failWrites)
    {
        BrowserTestRuntime script = new() { FailWrites = failWrites };
        script.Local["contents.ui.experimental"] = original ? "true"
                                                            : "false";
        SettingsActivator activator = new();
        using ServiceProvider provider = Services(script, activator);
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent output = await renderer.RenderComponentAsync<ExperimentalSettingsPanel>().ConfigureAwait(true);
            Assert.Equal(original, output.ToHtmlString().Contains(" checked", StringComparison.Ordinal));
            Assert.Contains("Always enable experimental features in this browser", output.ToHtmlString(), StringComparison.Ordinal);
            ExperimentalSettingsPanel component = activator.Component!;
            typeof(ExperimentalSettingsPanel).GetField("enabled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, !original);
            Func<Task> save = typeof(ExperimentalSettingsPanel).GetMethod("SaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Func<Task>>(component);
            await ((IHandleEvent)component).HandleEventAsync(new EventCallbackWorkItem(save), null).ConfigureAwait(true);
            string expected = failWrites ? "The setting could not be saved."
                                         : "Settings saved for this browser.";
            Assert.Contains(expected, output.ToHtmlString(), StringComparison.Ordinal);
            HtmlRootComponent reopened = await renderer.RenderComponentAsync<ExperimentalSettingsPanel>().ConfigureAwait(true);
            bool persisted = failWrites ? original
                                        : !original;
            Assert.Equal(persisted, reopened.ToHtmlString().Contains(" checked", StringComparison.Ordinal));
        }).ConfigureAwait(true);
        Assert.Empty(script.Session);
        Assert.Single(script.Local);
    }

    /// <summary>保存領域の読取りに失敗しても、画面を表示し直して再保存できます。</summary>
    /// <returns>読取り障害と復帰時の確認です。</returns>
    [Fact]
    public async Task FailedReadIsVisibleAndDoesNotPreventSavingAsync()
    {
        BrowserTestRuntime script = new() { RejectRead = _ => true };
        SettingsActivator activator = new();
        using ServiceProvider provider = Services(script, activator);
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent output = await renderer.RenderComponentAsync<ExperimentalSettingsPanel>().ConfigureAwait(true);
            Assert.Contains("The setting could not be read.", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.DoesNotContain("disabled", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Empty(script.Local);
            script.RejectRead = null;
            ExperimentalSettingsPanel component = activator.Component!;
            typeof(ExperimentalSettingsPanel).GetField("enabled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, true);
            Func<Task> save = typeof(ExperimentalSettingsPanel).GetMethod("SaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Func<Task>>(component);
            await ((IHandleEvent)component).HandleEventAsync(new EventCallbackWorkItem(save), null).ConfigureAwait(true);
            Assert.Contains("Settings saved for this browser.", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Equal("true", script.Local["contents.ui.experimental"]);
        }).ConfigureAwait(true);
    }

    /// <summary>Coreへの接続を登録せず、ブラウザー保存だけで画面を構成します。</summary>
    /// <returns>呼出元で破棄するサービスです。</returns>
    private static ServiceProvider Services(BrowserTestRuntime script, SettingsActivator activator)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton<BrowserStorage>();
        services.AddSingleton(provider => new UiText(provider.GetRequiredService<BrowserStorage>()));
        services.AddSingleton<IComponentActivator>(activator);
        return services.BuildServiceProvider();
    }

    /// <summary>保存イベントを呼ぶため、生成された画面を保持します。</summary>
    private sealed class SettingsActivator : IComponentActivator
    {
        internal ExperimentalSettingsPanel? Component
        {
            get; private set;
        }

        /// <summary>通常のコンポーネント生成を維持します。</summary>
        /// <returns>実Razorコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is ExperimentalSettingsPanel settings)
            {
                Component = settings;
            }
            return component;
        }
    }
}
