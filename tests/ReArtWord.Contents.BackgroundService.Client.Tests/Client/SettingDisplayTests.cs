using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実際の設定表示が入力優先と日本語ラベルを保つことを検証します。</summary>
public sealed class SettingDisplayTests
{
    /// <summary>PC選択の名前・説明を日本語で表示し、欠測をゼロとして描画しません。</summary>
    /// <returns>実際のRazor描画とカタログ翻訳の確認。</returns>
    [Fact]
    public async Task PcSelectionNamesAndUnavailableObservationsAreVisibleAsync()
    {
        UiText ui = new(new(new BrowserTestRuntime()));
        await ui.ChangeAsync("ja").ConfigureAwait(true);
        NodeDescriptor descriptor = new(PcSelectionPorts.SelectNode, "Select PCs", "Execution", "Choose PCs", ENodeKind.Action,
            EExecutionLocation.Independent, [], [], [], JsonSerializer.SerializeToElement(new { }));
        Assert.Equal("PC選択", ui.NodeName(descriptor));
        Assert.Equal("低負荷PC選択", ui.NodeName(descriptor with { Type = PcSelectionPorts.LowestNode }));
        Assert.Contains("クラスタ内", ui.NodeDescription(descriptor), StringComparison.Ordinal);
        DateTimeOffset time = DateTimeOffset.UnixEpoch;
        PcSelectionDecision selection = new(time, ["missing-pc"], [new("missing-pc", "missing-peer", Guid.NewGuid(), EPcResource.Cpu, null, "percent", null, Reason: "selection.observationMissing")]);
        ExecutionReport report = new(Guid.NewGuid(), "workflow", "revision", EOutcome.NotExecuted, time, time, [new("node", EOutcome.NotExecuted, [], PcSelection: selection)]);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(ui);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { [nameof(PcSelectionDetails.Report)] = report };
            HtmlRootComponent output = await renderer.RenderComponentAsync<PcSelectionDetails>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        }).ConfigureAwait(true);
        Assert.Contains("missing-pc", html, StringComparison.Ordinal);
        Assert.Contains(ui["Unavailable"] + " percent", html, StringComparison.Ordinal);
        Assert.Contains(ui["selection.observationMissing"], html, StringComparison.Ordinal);
        Assert.DoesNotContain(">0 percent", html, StringComparison.Ordinal);
    }

    /// <summary>定数入力と配線の両方で保存設定を読み取り専用表示し、値は変更しません。</summary>
    /// <returns>描画結果の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedInputDisablesFallbackWithoutChangingItAsync(bool wire)
    {
        EditorDocument model = new(new(new BrowserTestRuntime()));
        JsonElement settings = JsonSerializer.Deserialize<JsonElement>("{\"method\":\"GET\"}");
        NodeDefinition node = new("n", "http.request", settings);
        IReadOnlyList<WireDefinition> wires = [];
        if (wire)
        {
            wires = [new("source", "value", "n", "method")];
        }
        else
        {
            node = node with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["method"] = NodeValue.FromObject("POST") } };
        }
        WorkflowDefinition workflow = new("flow", "flow", [node], wires);
        await model.LoadAsync(new(workflow, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        UiText ui = new(new(new BrowserTestRuntime()));
        await ui.ChangeAsync("ja").ConfigureAwait(true);
        using HttpClient http = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(model);
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(ui);
        services.AddSingleton(new SettingChoiceProvider(new ManagementApi(http)));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using (renderer.ConfigureAwait(true))
        {
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                [nameof(SchemaFields.NodeId)] = "n",
                [nameof(SchemaFields.Schema)] = JsonSerializer.Deserialize<JsonElement>("{\"properties\":{\"method\":{\"type\":\"string\",\"enum\":[\"GET\",\"POST\"]}}}"),
                [nameof(SchemaFields.Settings)] = settings,
                [nameof(SchemaFields.Inputs)] = new PortDescriptor[] { new("method", EPortValueKind.Json, OverridesSetting: "method") },
            };
            HtmlRootComponent output = await renderer.RenderComponentAsync<SchemaFields>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            return output.ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Contains("disabled", html, StringComparison.Ordinal);
        Assert.Contains("GET", html, StringComparison.Ordinal);
        Assert.Contains("入力値を使用", System.Net.WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Equal("GET", model.Draft.Document.Nodes[0].Settings.GetProperty("method").GetString());
        Assert.Equal("ピアID", ui["Peer ID"]);
        Assert.Equal("ホストグループ", ui["Host Group"]);
        }
    }
}
