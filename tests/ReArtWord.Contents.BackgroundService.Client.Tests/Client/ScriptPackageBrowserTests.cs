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
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>パッケージの選択と導入を分け、古い照会応答の混入を防ぎます。</summary>
public sealed class ScriptPackageBrowserTests
{
    /// <summary>検索や候補選択では導入せず、遅延した詳細を現在の候補へ上書きしません。</summary>
    /// <returns>実Razorの検索・詳細表示・世代管理です。</returns>
    [Fact]
    public async Task SearchAndSelectionNeverInstallOrApplyStaleDetailsAsync()
    {
        TaskCompletionSource<HttpResponseMessage> delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ClientHttpHandler handler = new(async (message, token) =>
        {
            string path = message.RequestUri!.AbsolutePath;
            switch (path)
            {
                case "/client/script-packages/search":
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ScriptPackageSearchResult([new("first", "1.0.0", "First package"), new("second", "2.0.0", "Second package")], false), options: ContractJson.Options) };
                case "/client/script-packages/details":
                    ScriptPackageDetailsRequest request = (await message.Content!.ReadFromJsonAsync<ScriptPackageDetailsRequest>(ContractJson.Options, token).ConfigureAwait(true))!;
                    string first = "first";
                    if (string.Equals(request.Id, first, StringComparison.Ordinal))
                    {
                        return await delayed.Task.ConfigureAwait(true);
                    }
                    return Details("second", "Current description");
                default:
                    throw new InvalidOperationException("Search must not invoke installation.");
            }
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        BrowserTestRuntime browser = new();
        Activator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(browser);
        services.AddSingleton(new UiText(new(browser)));
        services.AddSingleton(new ManagementApi(http));
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            int installs = 0;
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                [nameof(ScriptPackageBrowser.Kind)] = EScriptPackageKind.Npm,
                [nameof(ScriptPackageBrowser.Install)] = EventCallback.Factory.Create<ScriptPackageReference>(this, _ => installs++),
            };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent rendered = await renderer.RenderComponentAsync<ScriptPackageBrowser>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            ScriptPackageBrowser component = activator.Browser!;
            MethodInfo search = typeof(ScriptPackageBrowser).GetMethod("SearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo select = typeof(ScriptPackageBrowser).GetMethod("SelectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task)search.Invoke(component, ["package", 0, CancellationToken.None])!).ConfigureAwait(true);
            await component.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("First package", rendered.ToHtmlString(), StringComparison.Ordinal);
            using Task old = (Task)select.Invoke(component, ["first", CancellationToken.None])!;
            await ((Task)select.Invoke(component, ["second", CancellationToken.None])!).ConfigureAwait(true);
            await component.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("Current description", rendered.ToHtmlString(), StringComparison.Ordinal);
            delayed.SetResult(Details("first", "Stale description"));
            await old.ConfigureAwait(true);
            await component.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.DoesNotContain("Stale description", rendered.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("Current description", rendered.ToHtmlString(), StringComparison.Ordinal);
            Assert.Equal(0, installs);
        }).ConfigureAwait(true);
    }

    /// <summary>公開版の詳細を通常の型付き応答で返します。</summary>
    /// <returns>呼出し側が所有するHTTP応答です。</returns>
    private static HttpResponseMessage Details(string id, string description) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ScriptPackageDetails(id, description, "2.0.0", ["2.0.0", "1.0.0"], new("https://www.npmjs.com/package/" + id)), options: ContractJson.Options),
    };

    /// <summary>表示されたコンポーネントの実イベント処理を呼び出すために保持します。</summary>
    private sealed class Activator : IComponentActivator
    {
        internal ScriptPackageBrowser? Browser
        {
            get; private set;
        }

        /// <summary>Blazorへ通常のコンポーネントを渡します。</summary>
        /// <returns>生成されたコンポーネントです。</returns>
        public IComponent CreateInstance(Type type)
        {
            IComponent component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is ScriptPackageBrowser browser)
            {
                Browser = browser;
            }
            return component;
        }
    }
}
