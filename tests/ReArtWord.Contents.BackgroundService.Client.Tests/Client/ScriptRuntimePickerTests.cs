using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Scripting;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>大量Nodeの描画で探索を開始せず、実行先を固定した選択・既定変更を検証します。</summary>
public sealed class ScriptRuntimePickerTests
{
    /// <summary>表示だけは無通信で、明示取得と既定変更は画面の実行先と比較版を維持します。</summary>
    /// <returns>実Razor描画とHTTP要求の確認です。</returns>
    [Fact]
    public async Task LoadsLazilyAndChangesDefaultOnlyOnDisplayedPeer()
    {
        int reads = 0, writes = 0;
        ScriptRuntimeCatalog catalog = new("python", "revision-one", null, "C:/Python/python.exe",
            [new("C:/Python/python.exe", "3.15.0", "PATH", false, true), new("C:/Old/python.exe", null, "registered", true, false, "script.runtime.unavailable")]);
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            Assert.Equal("peer-a", request.Headers.GetValues("X-Contents-Peer").Single());
            Assert.Equal("cluster-a", request.Headers.GetValues("X-Contents-Cluster").Single());
            string change = "/api/scripts/runtimes/change";
            if (string.Equals(request.RequestUri!.AbsolutePath, change, StringComparison.Ordinal))
            {
                writes++;
                ScriptRuntimeChange sent = (await request.Content!.ReadFromJsonAsync<ScriptRuntimeChange>(ContractJson.Options, token).ConfigureAwait(true))!;
                Assert.True(sent.SetDefault);
                Assert.Equal(catalog.Revision, sent.ExpectedRevision);
                Assert.Equal("C:/Python/python.exe", sent.Path);
                catalog = catalog with { Revision = "revision-two", DefaultPath = sent.Path };
            }
            else
            {
                reads++;
                Assert.Equal("/api/scripts/runtimes/list", request.RequestUri.AbsolutePath);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(catalog, options: ContractJson.Options) };
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http) { SelectedClusterId = "other-cluster", SelectedPeerId = "other-peer" };
        RuntimePickerActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(api);
        services.AddSingleton<IJSRuntime>(new BrowserTestRuntime());
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                [nameof(ScriptRuntimePicker.ClusterId)] = "cluster-a", [nameof(ScriptRuntimePicker.PeerId)] = "peer-a",
                [nameof(ScriptRuntimePicker.Language)] = "python", [nameof(ScriptRuntimePicker.Value)] = "C:/Python/python.exe", [nameof(ScriptRuntimePicker.Manage)] = true,
            };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent rendered = await renderer.RenderComponentAsync<ScriptRuntimePicker>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Equal(0, reads);
            Assert.Contains("Default runtime", rendered.ToHtmlString(), StringComparison.Ordinal);
            await InvokeAsync(activator.Picker!, "LoadAsync", [false, CancellationToken.None]).ConfigureAwait(true);
            Assert.Equal(1, reads);
            Assert.Equal(0, writes);
            await InvokeAsync(activator.Picker!, "LoadAsync", [false, CancellationToken.None]).ConfigureAwait(true);
            Assert.Equal(1, reads);
            await InvokeAsync(activator.Picker!, "SetDefaultAsync", [CancellationToken.None]).ConfigureAwait(true);
            Assert.Equal(1, writes);
            Assert.Equal("C:/Python/python.exe", catalog.DefaultPath);
        }).ConfigureAwait(true);
    }

    /// <summary>Rendererが初期化したコンポーネントのイベントと同じ処理を呼びます。</summary>
    /// <returns>実非同期ハンドラーの完了です。</returns>
    private static async Task InvokeAsync(ScriptRuntimePicker picker, string method, object[] arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ((Task)typeof(ScriptRuntimePicker).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, arguments)!).ConfigureAwait(false);
    }

    /// <summary>依存注入と実ライフサイクルを維持してコンポーネントを取得します。</summary>
    private sealed class RuntimePickerActivator : IComponentActivator
    {
        internal ScriptRuntimePicker? Picker
        {
            get; private set;
        }

        /// <summary>標準と同じコンポーネントを生成します。</summary>
        /// <returns>Rendererが初期化するインスタンスです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is ScriptRuntimePicker picker)
            {
                Picker = picker;
            }
            return component;
        }
    }
}
