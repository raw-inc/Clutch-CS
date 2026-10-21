using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>OFF・ON・状態未確認の表示を分け、状態照会でノードを実行しません。</summary>
public sealed class ToggleButtonRenderingTests
{
    /// <summary>確認済み状態だけを押下可能にし、アクセシビリティへも状態を渡します。</summary>
    /// <returns>実Razorのボタン表示です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(null, true)]
    public async Task RendersConfirmedStateWithoutChangingIt(bool? state, bool recipe)
    {
        BrowserTestRuntime script = new();
        EditorDocument model = new(new(script));
        WorkflowDefinition document = Definition() with { IsRecipe = recipe };
        await model.LoadAsync(new(document, "saved", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(model);
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(new UiText(new(script)));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        int presses = 0;
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                ["Node"] = document.Nodes[0], ["ToggleState"] = state,
                ["Pressed"] = EventCallback.Factory.Create<string>(this, _ => presses++),
            };
            return (await renderer.RenderComponentAsync<ButtonNodeControl>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Equal(state is null, html.Contains("disabled", StringComparison.Ordinal));
        string label = state switch { true => "ON", false => "OFF", _ => "?" };
        Assert.Contains(">" + label + "</span>", html, StringComparison.Ordinal);
        if (state is not null)
        {
            Assert.Contains("aria-pressed=\"" + JsonSerializer.Serialize(state.Value) + "\"", html, StringComparison.Ordinal);
        }
        Assert.Contains("Stub / live", html, StringComparison.Ordinal);
        Assert.Equal(0, presses);
    }

    /// <summary>初回OFFと取得不能を区別し、他のWFの表示へ状態を流用しません。</summary>
    /// <returns>共有状態APIだけの読取りと、切替後の再取得です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateLookupDistinguishesMissingStateFromUnavailableOrCorruptState(bool recipe)
    {
        using StateHandler handler = new();
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost/") };
        EditorToggleStates states = new(new ManagementApi(http));
        WorkflowDefinition document = Definition() with { IsRecipe = recipe };
        await states.RefreshAsync("cluster", "peer", document).ConfigureAwait(true);
        Assert.False(states.Find("cluster", document.Id)["toggle"]);
        Assert.Empty(states.Find("another", document.Id));
        Assert.Empty(states.Find("cluster", "another"));
        handler.Value = ContractJson.ToValue(new ToggleButtonState(true, []));
        states.Invalidate("toggle");
        await states.RefreshAsync("cluster", "peer", document).ConfigureAwait(true);
        Assert.True(states.Find("cluster", document.Id)["toggle"]);
        handler.Known = false;
        states.Invalidate("toggle");
        await states.RefreshAsync("cluster", "peer", document).ConfigureAwait(true);
        Assert.Null(states.Find("cluster", document.Id)["toggle"]);
        handler.Known = true;
        handler.Value = NodeValue.FromObject(new { corrupt = true });
        states.Invalidate("toggle");
        await states.RefreshAsync("cluster", "peer", document).ConfigureAwait(true);
        Assert.Null(states.Find("cluster", document.Id)["toggle"]);
        Assert.Equal(4, handler.Reads);
        states.Reset();
        Assert.Empty(states.Find("cluster", document.Id));
    }

    /// <summary>通常の二値切替を定義します。</summary>
    /// <returns>未実行の入力だけを持つWFです。</returns>
    private static WorkflowDefinition Definition() => new("toggle-ui", "Toggle UI",
        [new("toggle", "control.toggleButton", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue> { ["label"] = NodeValue.FromObject("Stub / live") })], []);

    /// <summary>読取りAPI以外が呼ばれた場合は試験を失敗させます。</summary>
    private sealed class StateHandler : HttpMessageHandler
    {
        internal NodeValue? Value
        {
            get; set;
        }

        internal bool Known
        {
            get; set;
        } = true;

        internal int Reads
        {
            get; private set;
        }

        /// <summary>保存値と保存確認の有無を分けた実際のDTOを返します。</summary>
        /// <returns>状態照会の応答です。</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/internal-data/read", request.RequestUri!.AbsolutePath);
            Assert.Equal("cluster", Assert.Single(request.Headers.GetValues("X-Contents-Cluster")));
            Reads++;
            AvailabilityState available = new(EStoragePurpose.Runtime, "toggle", "peer", null, null, null);
            InternalDataReadResult result = new(new(Value, null, available), Value, null, Known, new());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: ContractJson.Options) });
        }
    }
}
