using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>設定欄の再描画より先に次の入力が届く場合と、候補復旧の表示を検証します。</summary>
public sealed class SettingEditRecoveryTests
{
    /// <summary>同じ問題を持つ通信設定と通常設定を、最新ノードへの項目単位の編集として扱います。</summary>
    /// <returns>候補表示と連続編集の確認です。</returns>
    [Theory]
    [InlineData("http.listen")]
    [InlineData("websocket.listen")]
    [InlineData("osc.listen")]
    public async Task ConsecutiveEditsDoNotRestoreStaleSettingsAsync(string type)
    {
        JsonElement initial = JsonSerializer.SerializeToElement(new { interfaceId = "saved-nic", localAddress = "192.0.2.10", mode = "saved-mode" });
        NodeDefinition node = new("node", type, initial);
        WorkflowDefinition document = new("flow", "flow", [node], []);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        using HttpClient http = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(model);
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        services.AddSingleton(new SettingChoiceProvider(new(http)));
        services.AddSingleton<EditorPresentationCache>();
        FieldActivator activator = new();
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                [nameof(SchemaFields.NodeId)] = node.Id,
                [nameof(SchemaFields.Inline)] = true,
                [nameof(SchemaFields.Settings)] = initial,
                [nameof(SchemaFields.Schema)] = JsonSerializer.Deserialize<JsonElement>("""{"properties":{"interfaceId":{"type":"string"},"localAddress":{"type":"string"},"mode":{"type":"string","enum":["saved-mode","other"]}}}"""),
                [nameof(SchemaFields.SettingsChanged)] = EventCallback.Factory.Create<JsonElement>(new object(), value => model.ChangeNode(node.Id, current => current with { Settings = value })),
            };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent rendered = await renderer.RenderComponentAsync<SchemaFields>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("<option value=\"saved-nic\" selected", rendered.ToHtmlString(), StringComparison.Ordinal);
            Assert.Contains("<option value=\"saved-mode\" selected", rendered.ToHtmlString(), StringComparison.Ordinal);
            MethodInfo commit = typeof(SchemaFields).GetMethod("CommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task)commit.Invoke(activator.Field, ["interfaceId", "new-nic", "string", default(JsonElement)])!).ConfigureAwait(true);
            await ((Task)commit.Invoke(activator.Field, ["localAddress", "192.0.2.20", "string", default(JsonElement)])!).ConfigureAwait(true);
            await ((Task)commit.Invoke(activator.Field, ["mode", "changed", "string", default(JsonElement)])!).ConfigureAwait(true);
            JsonElement saved = model.Draft.Document.Nodes[0].Settings;
            Assert.Equal("new-nic", saved.GetProperty("interfaceId").GetString());
            Assert.Equal("192.0.2.20", saved.GetProperty("localAddress").GetString());
            Assert.Equal("changed", saved.GetProperty("mode").GetString());
        }).ConfigureAwait(true);
    }

    /// <summary>再描画を待たずに連続入力を届けるため、実際のRazor入力欄を保持します。</summary>
    private sealed class FieldActivator : IComponentActivator
    {
        internal SchemaFields? Field
        {
            get; private set;
        }

        /// <summary>通常のコンポーネント生成を維持します。</summary>
        /// <returns>生成したコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is SchemaFields fields)
            {
                Field = fields;
            }
            return component;
        }
    }
}
