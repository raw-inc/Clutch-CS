using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>言語選択・保存値・解析UIの境界を実Razorコンポーネントで検証します。</summary>
public sealed class CodeBlockEditingTests
{
    /// <summary>一つ選択しても残りの型欄を維持し、再解析前の型を確定済みとしません。</summary>
    /// <returns>連続選択・再解析・コード変更時の候補破棄の確認です。</returns>
    [Theory]
    [InlineData("javascript", "export function run(value) { return value; }")]
    [InlineData("python", "def run(value):\n    return value")]
    public async Task ManualTypeSelectionStaysVisibleUntilReanalysisAsync(string language, string code)
    {
        BrowserTestRuntime browser = new();
        EditorDocument model = new(new(browser));
        ScriptAnalysisRequest request = new(language, code);
        ScriptContract hints = new(new Dictionary<string, ValueTypeDefinition>(StringComparer.Ordinal) { ["value"] = new("any") }, new("any"));
        ScriptAnalysis report = new(ContractJson.Hash(request), null, ["Choose types"], hints);
        NodeDefinition node = new("script", "script." + language, JsonSerializer.SerializeToElement(new { code, analysis = report }, ContractJson.Options));
        await model.LoadAsync(new(new("flow", "Flow", [node], []), "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        using ClientHttpHandler handler = new(async (message, token) =>
        {
            ScriptAnalysisRequest input = JsonSerializer.Deserialize<ScriptAnalysisRequest>(await message.Content!.ReadAsStringAsync(token).ConfigureAwait(true), ContractJson.Options)!;
            ScriptContract contract = input.ManualContract.Deserialize<ScriptContract>(ContractJson.Options)!;
            return new(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(new ScriptAnalysis(ContractJson.Hash(input), contract, []), options: ContractJson.Options) };
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http) { SelectedPeerId = "peer" };
        FieldActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(browser);
        services.AddSingleton(model);
        services.AddSingleton(new UiText(new(browser)));
        services.AddSingleton(new EditorScriptAnalysis(model, api, new(api)));
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { [nameof(ScriptCodeField.NodeId)] = node.Id, [nameof(ScriptCodeField.Value)] = code };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent rendered = await renderer.RenderComponentAsync<ScriptCodeField>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            MethodInfo choose = typeof(ScriptCodeField).GetMethod("Choose", BindingFlags.Instance | BindingFlags.NonPublic)!;
            choose.Invoke(activator.Script, ["value", "string"]);
            await activator.Script!.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("Return type", rendered.ToHtmlString(), StringComparison.Ordinal);
            Assert.Null(ScriptSettings.Confirmed(node.Type, model.Draft.Document.Nodes[0].Settings));
            choose.Invoke(activator.Script, [null, "string"]);
            await activator.Script.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.Contains("Return type", rendered.ToHtmlString(), StringComparison.Ordinal);
            Assert.Null(ScriptSettings.Confirmed(node.Type, model.Draft.Document.Nodes[0].Settings));
            MethodInfo analyze = typeof(ScriptCodeField).GetMethod("AnalyzeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task)analyze.Invoke(activator.Script, [CancellationToken.None])!).ConfigureAwait(true);
            Assert.Equal("string", ScriptSettings.Confirmed(node.Type, model.Draft.Document.Nodes[0].Settings)!.Contract!.Result.Type);
            model.ChangeNode(node.Id, current => current with { Settings = JsonSerializer.SerializeToElement(new { code = "changed" }) });
            parameters[nameof(ScriptCodeField.Value)] = "changed";
            await activator.Script.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            Assert.DoesNotContain("Return type", rendered.ToHtmlString(), StringComparison.Ordinal);
        }).ConfigureAwait(true);
    }

    /// <summary>色分け言語を変更してもコードを保ち、未対応の解析要求を送信しません。</summary>
    /// <returns>実際の入力処理・再描画・Undoの確認です。</returns>
    [Theory]
    [InlineData("bash")]
    [InlineData("powershell")]
    [InlineData("json")]
    [InlineData("toml")]
    [InlineData("yaml")]
    public async Task LanguageSelectionPreservesTextAndDoesNotRequestAnalysisAsync(string language)
    {
        BrowserTestRuntime script = new();
        EditorDocument model = new(new(script));
        string code = "日本語\n<value> & text";
        NodeDefinition node = new("code", "value.code", JsonSerializer.SerializeToElement(new { code, language = "text", pythonPath = "saved-path" }));
        await model.LoadAsync(new(new("flow", "Flow", [node], []), "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        int requests = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            requests++;
            throw new InvalidOperationException("Highlighting must not request script analysis.");
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ManagementApi api = new(http) { SelectedPeerId = "peer" };
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton(model);
        services.AddSingleton(new UiText(new(script)));
        services.AddSingleton(new EditorScriptAnalysis(model, api, new(api)));
        FieldActivator activator = new();
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { [nameof(CodeBlockFields.NodeId)] = node.Id };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent rendered = await renderer.RenderComponentAsync<CodeBlockFields>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            foreach (string choice in CodeBlockLanguages.All)
            {
                Assert.Contains("<option value=\"" + choice + "\"", rendered.ToHtmlString(), StringComparison.Ordinal);
            }
            MethodInfo change = typeof(CodeBlockFields).GetMethod("Change", BindingFlags.NonPublic | BindingFlags.Instance)!;
            change.Invoke(activator.Block, ["language", language]);
            await activator.Block!.SetParametersAsync(ParameterView.FromDictionary(parameters)).ConfigureAwait(true);
            JsonElement saved = model.Draft.Document.Nodes[0].Settings;
            Assert.Equal(language, saved.GetProperty("language").GetString());
            Assert.Equal(code, saved.GetProperty("code").GetString());
            Assert.Equal("saved-path", saved.GetProperty("pythonPath").GetString());
            Dictionary<string, object?> field = new(StringComparer.Ordinal) { [nameof(ScriptCodeField.NodeId)] = node.Id, [nameof(ScriptCodeField.Value)] = code };
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent inspector = await renderer.RenderComponentAsync<ScriptCodeField>(ParameterView.FromDictionary(field)).ConfigureAwait(true);
            Assert.DoesNotContain("Analyze code and types", inspector.ToHtmlString(), StringComparison.Ordinal);
            MethodInfo blur = typeof(ScriptCodeField).GetMethod("AnalyzeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await ((Task)blur.Invoke(activator.Script, [CancellationToken.None])!).ConfigureAwait(true);
            Assert.Equal(0, requests);
            model.Undo();
            Assert.Equal("text", model.Draft.Document.Nodes[0].Settings.GetProperty("language").GetString());
            Assert.Equal(code, model.Draft.Document.Nodes[0].Settings.GetProperty("code").GetString());
        }).ConfigureAwait(true);
    }

    /// <summary>実際のイベント処理を呼び出すため、描画された入力コンポーネントを保持します。</summary>
    private sealed class FieldActivator : IComponentActivator
    {
        internal CodeBlockFields? Block
        {
            get; private set;
        }

        internal ScriptCodeField? Script
        {
            get; private set;
        }

        /// <summary>通常の初期化と同じコンポーネントを作成します。</summary>
        /// <returns>依存関係注入前の実コンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is CodeBlockFields block)
            {
                Block = block;
            }
            if (component is ScriptCodeField script)
            {
                Script = script;
            }
            return component;
        }
    }
}
