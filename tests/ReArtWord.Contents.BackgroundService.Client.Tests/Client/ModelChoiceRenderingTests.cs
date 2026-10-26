using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>モデル候補を表示しながら、候補外の接続値をエラーにせず確認できます。</summary>
public sealed class ModelChoiceRenderingTests
{
    private static readonly string[] models = ["known-model"];

    /// <summary>入力候補は任意の文字列を妨げず、列挙型の設定は従来の制約を表示します。</summary>
    /// <returns>設定欄の候補・プレビュー・保存値の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SuggestionsPreserveCustomModelsAndSelectionAsync(bool connected, bool constrained)
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { model = connected ? "known-model"
                                                                                         : "custom-model" });
        NodeDefinition target = new("target", "ai.audio.qwen.audio3.generate", settings);
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { type = "string", text = "custom-model" }));
        IReadOnlyList<WireDefinition> wires = [];
        if (connected)
        {
            wires = [new("source", "value", "target", SettingPorts.Name("model"))];
        }
        WorkflowDefinition document = new("models", "Models", [source, target], wires);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        UiText ui = new(new(new BrowserTestRuntime()));
        await ui.ChangeAsync("en").ConfigureAwait(true);
        using HttpClient http = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(model);
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(ui);
        services.AddSingleton(new SettingChoiceProvider(new ManagementApi(http)));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string choiceKey = constrained ? "enum"
                                       : "examples";
        JsonElement schema = JsonSerializer.SerializeToElement(new
        {
            properties = new { model = new Dictionary<string, object>(StringComparer.Ordinal) { ["type"] = "string", [choiceKey] = models } },
        });
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                [nameof(SchemaFields.NodeId)] = target.Id,
                [nameof(SchemaFields.Settings)] = settings,
                [nameof(SchemaFields.Schema)] = schema,
            };
            return (await renderer.RenderComponentAsync<SchemaFields>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Contains("<select", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"known-model\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"custom-model\" selected", html, StringComparison.Ordinal);
        Assert.Equal(connected, html.Contains("disabled", StringComparison.Ordinal));
        Assert.Equal(constrained, html.Contains(ui["The saved value is not in the current choices."], StringComparison.Ordinal));
        Assert.Equal(constrained, html.Contains(ui["Not in current choices"], StringComparison.Ordinal));
        Assert.Equal(ContractJson.Hash(document), ContractJson.Hash(model.Draft.Document));
        Assert.False(model.Draft.Dirty);
    }
}
