using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Operations.Scripting;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実Razor画面の操作が、表示したPeerと構成版へ一度だけ送られることを検査します。</summary>
public sealed class ScriptPackagePanelTests
{
    /// <summary>パッケージ方式を切り替えても環境選択と導入UIが重複キーで描画失敗しません。</summary>
    /// <returns>既存画面の連続切替と実Razor差分描画です。</returns>
    [Fact]
    public async Task SwitchingPackageSourcesRendersRuntimeControlsAsync()
    {
        BrowserTestRuntime browser = new();
        using ClientHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ScriptPackageCatalog(null, null, [], false), options: ContractJson.Options),
        }));
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http) { SelectedPeerId = "peer-a", SelectedClusterId = "cluster-a" };
        PanelActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(browser);
        services.AddSingleton(new UiText(new(browser)));
        services.AddSingleton(api);
        services.AddSingleton(new OperationJournal(api, new(browser)));
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent rendered = await renderer.RenderComponentAsync<ScriptPackagePanel>().ConfigureAwait(true);
            foreach (EScriptPackageKind source in new[] { EScriptPackageKind.Pip, EScriptPackageKind.Npm, EScriptPackageKind.Pip })
            {
                typeof(ScriptPackagePanel).GetField("kind", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activator.Panel, source);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activator.Panel, null);
                string html = rendered.ToHtmlString();
                Assert.Contains("Script runtime", html, StringComparison.Ordinal);
                Assert.Contains("Register and select", html, StringComparison.Ordinal);
                Assert.Contains("runtime</button>", html, StringComparison.Ordinal);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>接続先が途中で変更されても、表示元と操作履歴の識別子を維持します。</summary>
    /// <returns>画面描画、受付、履歴、取得先固定の確認です。</returns>
    [Theory]
    [InlineData(EScriptPackageKind.Nuget)]
    [InlineData(EScriptPackageKind.Npm)]
    public async Task InstallUsesDisplayedPeerAndRecordsOriginalRequestAsync(EScriptPackageKind kind)
    {
        BrowserTestRuntime browser = new();
        string revision = new('a', 64);
        ScriptPackageEnvironmentInfo environment = new(kind, revision, [new("example", "1.0.0")], [new("example", "1.0.0")], DateTimeOffset.UtcNow);
        ScriptPackageCatalog catalog = new(environment, null, [], false);
        if (kind == EScriptPackageKind.Npm)
        {
            catalog = catalog with { Nuget = null, Npm = environment };
        }
        ScriptPackageChangeRequest? sent = null;
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            Assert.Equal("peer-a", request.Headers.GetValues("X-Contents-Peer").Single());
            Assert.Equal("cluster-a", request.Headers.GetValues("X-Contents-Cluster").Single());
            if (request.Method == HttpMethod.Get)
            {
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(catalog, options: ContractJson.Options) };
            }
            Assert.Null(sent);
            sent = await request.Content!.ReadFromJsonAsync<ScriptPackageChangeRequest>(ContractJson.Options, token).ConfigureAwait(true);
            Assert.Equal(sent!.OperationId, Assert.Single(browser.Operations.Values).OperationId);
            ScriptPackageOperation operation = new(sent, ContractJson.Hash(sent), "installing", EOutcome.Unknown, false, DateTimeOffset.UtcNow);
            catalog = catalog with { Busy = true, Operations = [operation] };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(operation, options: ContractJson.Options) };
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http) { SelectedPeerId = "peer-a", SelectedClusterId = "cluster-a" };
        OperationJournal journal = new(api, new(browser));
        PanelActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(browser);
        services.AddSingleton(new UiText(new(browser)));
        services.AddSingleton(api);
        services.AddSingleton(journal);
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent rendered = await renderer.RenderComponentAsync<ScriptPackagePanel>().ConfigureAwait(true);
            string html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Script packages", html, StringComparison.Ordinal);
            Assert.Contains("peer-a", html, StringComparison.Ordinal);
            Assert.Contains("Remove from new analysis", html, StringComparison.Ordinal);
            Assert.Contains("npm · JavaScript", html, StringComparison.Ordinal);
            api.SelectedPeerId = "peer-b";
            api.SelectedClusterId = "cluster-b";
            MethodInfo change = typeof(ScriptPackagePanel).GetMethod("ChangeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (kind == EScriptPackageKind.Npm)
            {
                typeof(ScriptPackagePanel).GetField("nodePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activator.Panel, "/test/node");
                typeof(ScriptPackagePanel).GetField("npmPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activator.Panel, "/test/npm-cli.js");
            }
            await ((Task)change.Invoke(activator.Panel, [kind, new ScriptPackageReference("example", "2.0.0"), false, CancellationToken.None])!).ConfigureAwait(true);
            Assert.NotNull(sent);
            Assert.Equal(revision, sent.ExpectedRevision);
            Assert.Equal("2.0.0", sent.Package.Version);
            Assert.Equal(kind, sent.Kind);
            if (kind == EScriptPackageKind.Npm)
            {
                Assert.Equal("/test/node", sent.NodePath);
                Assert.Equal("/test/npm-cli.js", sent.NpmPath);
            }
            Assert.Equal("peer-a", Assert.Single(journal.Items).PeerId);
            Assert.Equal("unknown", OperationJournal.Outcome(journal.LastResult));
        }).ConfigureAwait(true);
    }

    /// <summary>描画後の実イベントを呼べるよう、初期化されたコンポーネントを保持します。</summary>
    private sealed class PanelActivator : IComponentActivator
    {
        internal ScriptPackagePanel? Panel
        {
            get; private set;
        }

        /// <summary>通常と同じ型を作成し、依存注入をRendererへ委ねます。</summary>
        /// <returns>生成したコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is ScriptPackagePanel panel)
            {
                Panel = panel;
            }
            return component;
        }
    }
}
