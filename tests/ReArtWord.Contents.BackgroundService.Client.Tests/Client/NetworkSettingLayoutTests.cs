using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>NIC方式ごとの実HTML設定行とSVG接続口が同じ行を参照することを確認します。</summary>
public sealed class NetworkSettingLayoutTests
{
    /// <summary>非表示NIC行を座標へ数えず、保存した設定と配線を変更しません。</summary>
    /// <returns>通信ノード共通の実描画確認です。</returns>
    [Theory]
    [InlineData("osc.udp.receive", false)]
    [InlineData("osc.udp.receive", true)]
    [InlineData("http.listen", false)]
    [InlineData("http.listen", true)]
    [InlineData("udp.receive", false)]
    [InlineData("udp.receive", true)]
    [InlineData("tcp.listen", false)]
    [InlineData("tcp.listen", true)]
    [InlineData("osc.tcp.listen", false)]
    [InlineData("osc.tcp.listen", true)]
    [InlineData("artnet.receive", false)]
    [InlineData("artnet.receive", true)]
    [InlineData("websocket.listen", false)]
    [InlineData("websocket.listen", true)]
    public async Task NetworkRowsAndSettingPortsShareTheirVisibleIndexAsync(string type, bool perHost)
    {
        BrowserTestRuntime script = new();
        EditorDocument model = new(new(script));
        string mode = "explicit";
        if (perHost)
        {
            mode = "perHost";
        }
        JsonElement settings = JsonSerializer.SerializeToElement(new
        {
            networkBindingMode = mode,
            interfaceId = "saved-nic",
            localAddress = "192.0.2.10",
            networkBindings = new[] { new { hostId = "pc", interfaceId = "pc-nic", localAddress = "192.0.2.20" } },
            localPort = 9000,
        });
        JsonElement schema = NetworkBindingSettings.Schema(JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","required":["interfaceId","localAddress"],"properties":{
              "interfaceId":{"type":"string"},"localAddress":{"type":"string"},
              "localPort":{"type":"integer","x-runtime-input":true},"maxBytes":{"type":"integer","x-runtime-input":true}}}
            """));
        NodeDescriptor target = new(type, type, "Communication", "", ENodeKind.Source, EExecutionLocation.ExplicitPeer, [], [new("value")], ["success"], schema);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NodeDescriptor source = new("external.port", "Port", "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value", EPortValueKind.Json)], ["success"], empty);
        WorkflowDefinition document = new("flow", "Network layout", [new("target", type, settings), new("source", source.Type, empty)], [new("source", "value", "target", "$settings.localPort")]);
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        string baseline = ContractJson.Hash(model.Draft.Document);
        using HttpClient http = new();
        ManagementApi api = new(http);
        ConnectionState connection = new(api);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton(model);
        services.AddSingleton(api);
        services.AddSingleton(connection);
        services.AddSingleton(new EditorDiagnostics());
        services.AddSingleton(new UiText(new(script)));
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(new EditorCollaboration(model, api));
        services.AddSingleton(new EditorNodeActivity(api));
        services.AddSingleton(new EditorPreviews(api));
        services.AddSingleton(new EditorConcurrencyQueues(api));
        services.AddSingleton(new SettingChoiceProvider(api, connection, model));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["Catalog"] = new[] { target, source }, ["ShowDetails"] = true };
            return (await renderer.RenderComponentAsync<NodeGraph>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        }).ConfigureAwait(true);
        string[] fields = Regex.Matches(html, "data-setting-key=\"([^\"]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(match => match.Groups[1].Value).ToArray();
        string[] expected = ["networkBindingMode", "interfaceId", "localAddress", "localPort", "maxBytes"];

        // value・start・result・sourceの4行、その下の設定見出しと固定高の設定行です。
        double expectedY = 462;
        if (perHost)
        {
            expected = ["networkBindingMode", "networkBindings", "localPort", "maxBytes"];
            expectedY = 398;
            Assert.Contains("class=\"node-inline-field\" data-setting-key=\"networkBindings\"", html, StringComparison.Ordinal);
        }
        Assert.Equal(expected, fields);
        Match circle = Regex.Match(html, "data-wire-port=\"\\$settings.localPort\"[\\s\\S]*?<circle[^>]*cy=\"([0-9.]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(circle.Success);
        Assert.Equal(expectedY, double.Parse(circle.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Contains("data-to-port=\"$settings.localPort\"", html, StringComparison.Ordinal);
        Assert.Equal(baseline, ContractJson.Hash(model.Draft.Document));
        Assert.False(model.Draft.Dirty);
    }
}
