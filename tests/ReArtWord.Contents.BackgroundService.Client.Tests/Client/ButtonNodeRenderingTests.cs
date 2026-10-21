using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>入力ラベルを安全に表示し、描画や未保存草稿では押下しません。</summary>
public sealed class ButtonNodeRenderingTests
{
    /// <summary>直接入力と接続入力を表示し、保存前の押下を無効にします。</summary>
    /// <returns>実Razorが出力するラベルとボタンの状態です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RendersInputLabelWithoutExecuting(bool connected, bool dirty)
    {
        string label = "Start <cue>";
        NodeDefinition button = new("button", "control.button", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue> { ["label"] = NodeValue.FromObject(label) });
        NodeDefinition source = new("label", "value.constant", JsonSerializer.SerializeToElement(new { type = "string", text = label }));
        WorkflowDefinition document = new("buttons", "Buttons", [button], []);
        if (connected)
        {
            document = document with { Nodes = [source, button with { Inputs = null }], Wires = [new("label", "value", "button", "label")] };
        }
        BrowserTestRuntime script = new();
        EditorDocument model = new(new(script));
        await model.LoadAsync(new(document, "saved", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        if (dirty)
        {
            model.Change(value => value with { Name = "Edited" });
        }
        int pressed = 0;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(model);
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(new UiText(new(script)));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                ["Node"] = document.Nodes.Single(node => node.Id is "button"),
                ["Pressed"] = EventCallback.Factory.Create<string>(this, _ => pressed++),
            };
            return (await renderer.RenderComponentAsync<ButtonNodeControl>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Contains("Start &lt;cue&gt;", html, StringComparison.Ordinal);
        Assert.Equal(dirty, html.Contains("disabled", StringComparison.Ordinal));
        Assert.Equal(0, pressed);
    }
}
