using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>WFの読取り結果を実通知コンポーネントへ渡し、空カードと必要な警告を区別します。</summary>
public sealed class EditorNoticeRenderingTests
{
    /// <summary>省略表示でも通知本文は読み上げ可能なまま、背面操作を妨げる閉じるボタンを出しません。</summary>
    /// <returns>通常表示と操作を持たない表示の描画確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactNoticesPreserveAccessibleMessagesWithoutInteractiveControlsAsync(bool compact)
    {
        EditorNoticeHub notices = new();
        notices.Set("operation", () => "Saved <result>\nShared successfully");
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        services.AddSingleton(notices);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<EditorNotifications>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(EditorNotifications.Compact)] = compact,
        })).ConfigureAwait(true)).ToHtmlString()).ConfigureAwait(true);
        Assert.Contains("role=\"status\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", html, StringComparison.Ordinal);
        Assert.Contains("Saved &lt;result&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Shared successfully", html, StringComparison.Ordinal);
        if (compact)
        {
            Assert.DoesNotContain("<button", html, StringComparison.Ordinal);
            Assert.DoesNotContain("tabindex", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("<button", html, StringComparison.Ordinal);
        }
    }

    /// <summary>通常のタブ移動ではカードを作らず、オフライン警告と退出時の本文は維持します。</summary>
    /// <returns>実読取り経路とRazor描画の検証です。</returns>
    [Fact]
    public async Task WorkflowNavigationOnlyDisplaysMeaningfulNoticesAsync()
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        UiText ui = new(storage);
        EditorNoticeHub notices = new();
        EditorDocument model = new(storage);
        bool offline = false;
        using ClientHttpHandler handler = new((request, _) =>
        {
            if (offline)
            {
                throw new HttpRequestException("offline");
            }
            string id = request.RequestUri!.Segments[^1];
            WorkflowDefinition definition = new(id, "saved-" + id, [], []);
            WorkflowView view = new(new(definition, "revision-" + id, DateTimeOffset.UnixEpoch, EActivationPolicy.Explicit), null, null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(view, options: ContractJson.Options) });
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(client) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        await model.SwitchTargetAsync("cluster", "peer").ConfigureAwait(true);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(ui);
        services.AddSingleton(notices);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent output = await renderer.RenderComponentAsync<EditorNotifications>().ConfigureAwait(true);
            string[] workflowIds = ["a", "b", "a"];
            foreach (string id in workflowIds)
            {
                notices.Set("operation", ui.Deferred(await EditorWorkflowLoader.OpenAsync(model, api, storage, id).ConfigureAwait(true)));
                Assert.Equal(id, model.Draft.Document.Id);
                Assert.DoesNotContain("<aside", output.ToHtmlString(), StringComparison.Ordinal);
            }

            offline = true;
            string warningMessage = await EditorWorkflowLoader.OpenAsync(model, api, storage, "b").ConfigureAwait(true);
            notices.Set("operation", ui.Deferred(warningMessage));
            string warning = WebUtility.HtmlDecode(output.ToHtmlString());
            Assert.Contains("<aside", warning, StringComparison.Ordinal);
            Assert.Contains(ui[warningMessage], warning, StringComparison.Ordinal);
            Assert.DoesNotContain("<p></p>", warning, StringComparison.Ordinal);

            offline = false;
            notices.Set("operation", ui.Deferred(await EditorWorkflowLoader.OpenAsync(model, api, storage, "a").ConfigureAwait(true)));
            string leaving = WebUtility.HtmlDecode(output.ToHtmlString());
            Assert.Empty(notices.Items);
            Assert.Single(leaving.Split("<aside", StringSplitOptions.None).Skip(1));
            Assert.Contains("aria-hidden=\"true\"", leaving, StringComparison.Ordinal);
            Assert.Contains(ui[warningMessage], leaving, StringComparison.Ordinal);
            Assert.DoesNotContain("<p></p>", leaving, StringComparison.Ordinal);
        }).ConfigureAwait(true);
    }
}
