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
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Operations.Resources;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>登録削除の確認、対象の固定、未確認結果の表示を実RazorとHTTP要求で検証します。</summary>
public sealed class ApplicationRemovalTests
{
    /// <summary>確認の取消しでは送信せず、確定時も元のホスト・登録版だけを対象にします。</summary>
    /// <returns>削除要求と、成功・失敗・結果不明に応じた表示です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded)]
    [InlineData(EOutcome.Failed)]
    [InlineData(EOutcome.Unknown)]
    [InlineData(null)]
    public async Task RemovalUsesConfirmedTargetAndOnlyClosesOnSuccessAsync(EOutcome? outcome)
    {
        ProcessView original = new("version-1", new("player", EProcessMode.Managed, "C:\\Apps\\Player.exe", [], "C:\\Apps", LogicalName: "Player"), new(EOutcome.Succeeded, null));
        List<ProcessView> registrations = [original];
        List<ProcessRemovalRequest> removals = [];
        ServiceStatus status = new("peer", Guid.NewGuid(), "peer", EPeerStatus.Ready, [], [], [], null, JsonSerializer.SerializeToElement(new { }));
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            Assert.Equal("cluster", Assert.Single(request.Headers.GetValues("X-Contents-Cluster")));
            Assert.Equal("peer", Assert.Single(request.Headers.GetValues("X-Contents-Peer")));
            object? value = null;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/status": value = status; break;
                case "/api/processes": value = registrations; break;
                case "/api/processes/remove":
                    Assert.Equal(HttpMethod.Post, request.Method);
                    removals.Add((await request.Content!.ReadFromJsonAsync<ProcessRemovalRequest>(ContractJson.Options, token).ConfigureAwait(false))!);
                    if (outcome == EOutcome.Succeeded)
                    {
                        registrations.Clear();
                    }
                    value = new { outcome };
                    break;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) };
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        PanelActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(new BrowserTestRuntime());
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        services.AddSingleton(api);
        services.AddSingleton<ConnectionState>();
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<ProcessPanel>().ConfigureAwait(true);
            ProcessPanel panel = activator.Panel!;
            Assert.Contains(">Remove registration</button>", output.ToHtmlString(), StringComparison.Ordinal);
            Invoke(panel, "ConfirmRemoval", original);
            await panel.SetParametersAsync(ParameterView.Empty).ConfigureAwait(true);
            Assert.Contains("A running application will not be stopped.", output.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("peer / cluster / peer", output.ToHtmlString(), StringComparison.Ordinal);
            Invoke(panel, "CloseRemoval");
            Assert.Empty(removals);
            Invoke(panel, "ConfirmRemoval", original);
            registrations[0] = original with { Version = "version-2" };
            await panel.RefreshAsync().ConfigureAwait(true);
            api.SelectedClusterId = "other-cluster";
            api.SelectedPeerId = "other-peer";
            await ((Task)Invoke(panel, "RemoveAsync", CancellationToken.None)!).ConfigureAwait(true);
            await panel.SetParametersAsync(ParameterView.Empty).ConfigureAwait(true);
            Assert.Equal(new ProcessRemovalRequest("player", "version-1"), Assert.Single(removals));
            string html = output.ToHtmlString();
            if (outcome == EOutcome.Succeeded)
            {
                Assert.DoesNotContain("<dialog", html, StringComparison.Ordinal);
                Assert.Contains("Application registration removed.", html, StringComparison.Ordinal);
                Assert.Contains("No applications registered on this host.", html, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("<dialog", html, StringComparison.Ordinal);
                Assert.Contains("Player", html, StringComparison.Ordinal);
                Assert.DoesNotContain("Application registration removed.", html, StringComparison.Ordinal);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>画面のボタンと同じイベント処理を呼びます。</summary>
    /// <returns>非同期処理の場合は、その完了を待つTaskです。</returns>
    private static object? Invoke(ProcessPanel panel, string name, params object?[] arguments)
        => typeof(ProcessPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, arguments);

    /// <summary>通常のコンポーネント生成を保ち、イベントの呼出し先だけを保持します。</summary>
    private sealed class PanelActivator : IComponentActivator
    {
        internal ProcessPanel? Panel
        {
            get; private set;
        }

        /// <summary>実Razorの初期化をRendererへ委ねます。</summary>
        /// <returns>生成したコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is ProcessPanel panel)
            {
                Panel = panel;
            }
            return component;
        }
    }
}
