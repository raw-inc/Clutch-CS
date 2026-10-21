using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>通常入力と同じ意味の設定口を重ねず、旧配線の端点を維持します。</summary>
public sealed class SettingPortRenderingTests
{
    /// <summary>OSCの両送信形式と同じ上書き構造を持つHTTP・ログにも同じ表示を適用します。</summary>
    /// <returns>実際のSVGに必要な接続口だけが存在することです。</returns>
    [Theory]
    [InlineData("osc.udp.send", "address", false)]
    [InlineData("osc.udp.send", "address", true)]
    [InlineData("osc.tcp.request", "address", false)]
    [InlineData("osc.tcp.request", "address", true)]
    [InlineData("http.request", "method", false)]
    [InlineData("control.log", "message", false)]
    public async Task OverrideInputHidesOnlyUnusedSettingConnector(string type, string property, bool legacyWire)
    {
        BrowserTestRuntime script = new();
        EditorDocument model = new(new(script));
        JsonElement settings = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [property] = "/original" });
        NodeDefinition node = new("target", type, settings);
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { value = "/changed" }));
        string settingPort = SettingPorts.Name(property);
        WorkflowDefinition document = new("flow", "flow", [node, source], legacyWire ? [new("source", "value", "target", settingPort)]
                                                                                     : []);
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        string baseline = ContractJson.Hash(model.Draft.Document);
        JsonElement schema = JsonSerializer.SerializeToElement(new { properties = new Dictionary<string, object> { [property] = new { type = "string" } } });
        NodeDescriptor descriptor = new(type, type, "Communication", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer,
            [new(property, EPortValueKind.Json, OverridesSetting: property)], [], ["success"], schema);
        using HttpClient http = new();
        ManagementApi api = new(http);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton(model);
        services.AddSingleton(new EditorDiagnostics());
        services.AddSingleton(new UiText(new(script)));
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(new EditorCollaboration(model, api));
        services.AddSingleton(new EditorNodeActivity(api));
        services.AddSingleton(new EditorPreviews(api));
        services.AddSingleton(new EditorConcurrencyQueues(api));
        services.AddSingleton(new SettingChoiceProvider(api));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["Catalog"] = new NodeDescriptor[] { descriptor }, ["ShowDetails"] = true };
            return (await renderer.RenderComponentAsync<NodeGraph>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Contains("data-wire-port=\"" + property + "\"", html, StringComparison.Ordinal);
        Assert.Equal(legacyWire, html.Contains("data-wire-port=\"" + settingPort + "\"", StringComparison.Ordinal));
        Assert.Contains(legacyWire ? "data-to-port=\"" + settingPort + "\""
                                   : "/original", html, StringComparison.Ordinal);
        Assert.Equal(baseline, ContractJson.Hash(model.Draft.Document));
        Assert.False(model.Draft.Dirty);
    }
}
