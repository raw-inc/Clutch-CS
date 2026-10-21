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
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実行を伴わない接続値プレビューと読み取り専用表示を検証します。</summary>
public sealed class ConnectedValuePreviewTests
{
    /// <summary>定数の型を保ち、曖昧な数値変換は確定値として表示しません。</summary>
    [Theory]
    [InlineData("string", "hello", "\"hello\"")]
    [InlineData("integer", "9007199254740993", "9007199254740993")]
    [InlineData("byte", "255", "255")]
    [InlineData("number", "1.25", "1.25")]
    [InlineData("decimal", "1.2345678901234567890123456789", "1.2345678901234567890123456789")]
    [InlineData("byte", "256", null)]
    [InlineData("integer", "1.5", null)]
    [InlineData("number", "1e-9999", null)]
    [InlineData("decimal", "1.234567890123456789012345678901", null)]
    [InlineData("auto", "null", "null")]
    [InlineData("object", "{\"a\":42}", "{\"a\":42}")]
    public void LiteralValuesAreBoundedAndTyped(string type, string text, string? expected)
    {
        WorkflowDefinition document = Definition(type, text);
        EditorDraft draft = Draft(document);
        string revision = ContractJson.Hash(document);
        NodeValue? value = new ConnectedValuePreview(draft).Input(document.Nodes[1], "message");
        Assert.Equal(expected, value?.Json.GetRawText());
        Assert.Equal(revision, ContractJson.Hash(document));
        Assert.False(draft.Dirty);
    }

    /// <summary>未確定・無効な編集では保存値を表示せず、次の草稿で最新値を確認します。</summary>
    [Fact]
    public void UnknownAndInvalidSourcesNeverShowStaleFallback()
    {
        WorkflowDefinition document = Definition("string", "current");
        EditorDraft draft = Draft(document);
        NodeDefinition target = document.Nodes[1];
        PortDescriptor[] ports = [new("message", OverridesSetting: "message")];
        Assert.Equal("current", new ConnectedValuePreview(draft).Setting(target, "message", ports)!.GetString());
        EditorDraft invalid = draft with { InputDrafts = new Dictionary<string, string>(StringComparer.Ordinal) { ["source/settings/text"] = "invalid" } };
        Assert.Null(new ConnectedValuePreview(invalid).Setting(target, "message", ports));
        NodeDefinition dynamic = document.Nodes[0] with { Type = "script.python" };
        Assert.Null(new ConnectedValuePreview(Draft(document with { Nodes = [dynamic, target] })).Input(target, "message"));
        WorkflowDefinition changed = Definition("string", "changed");
        Assert.Equal("changed", new ConnectedValuePreview(Draft(changed)).Input(changed.Nodes[1], "message")!.GetString());
    }

    /// <summary>コメントのパススルーとJSON内部抽出にも同じ確認値を使用します。</summary>
    [Fact]
    public void PassthroughAndPointerPreviewTheConnectedValue()
    {
        WorkflowDefinition document = Definition("object", "{\"age\":42}");
        NodeDefinition comment = new("comment", "value.comment", JsonSerializer.SerializeToElement(new { comment = "age" }));
        document = document with
        {
            Nodes = [document.Nodes[0], document.Nodes[1], comment],
            Wires = [new("source", "value", "comment", "value"), new("comment", "value", "target", "message", Path: "/age")],
        };
        Assert.Equal(42, new ConnectedValuePreview(Draft(document)).Input(document.Nodes[1], "message")!.Json.GetInt32());
    }

    /// <summary>通常入力・設定入力を、カードとインスペクターの両方で読み取り専用表示します。</summary>
    /// <returns>Razorの出力と保存内容の確認です。</returns>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public async Task ConnectedFieldsRenderPreviewWithoutChangingSavedData(bool setting, bool inline, bool known)
    {
        WorkflowDefinition document = Definition("string", "live preview");
        string port = setting ? SettingPorts.Name("message")
                              : "message";
        NodeDefinition source = document.Nodes[0];
        if (!known)
        {
            source = source with { Type = "http.request" };
        }
        document = document with { Nodes = [source, document.Nodes[1]], Wires = [new("source", "value", "target", port)] };
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
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["Inline"] = inline };
            if (setting)
            {
                parameters["NodeId"] = "target";
                parameters["Settings"] = document.Nodes[1].Settings;
                parameters["Schema"] = JsonSerializer.Deserialize<JsonElement>("""{"properties":{"message":{"type":"string"}}}""");
                return (await renderer.RenderComponentAsync<SchemaFields>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
            }
            parameters["Node"] = document.Nodes[1];
            parameters["Ports"] = new PortDescriptor[] { new("message", EPortValueKind.Json) };
            return (await renderer.RenderComponentAsync<NodeInputFields>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Contains("disabled", html, StringComparison.Ordinal);
        Assert.DoesNotContain("saved fallback", html, StringComparison.Ordinal);
        string expected = known ? "live preview"
                                : "not available without execution";
        Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.Equal(ContractJson.Hash(document), ContractJson.Hash(model.Draft.Document));
        Assert.False(model.Draft.Dirty);
    }

    /// <summary>保存版と比較元を同一にした未変更草稿です。</summary>
    /// <returns>テスト用の編集状態です。</returns>
    private static EditorDraft Draft(WorkflowDefinition document) => new(document, document, "base", document, null, null, false, []);

    /// <summary>保存フォールバックとは異なる定数を接続します。</summary>
    /// <returns>プレビュー対象グラフです。</returns>
    private static WorkflowDefinition Definition(string type, string text) => new("preview", "preview",
        [new("source", "value.constant", JsonSerializer.SerializeToElement(new { type, text })),
         new("target", "control.log", JsonSerializer.SerializeToElement(new { message = "saved fallback" }))],
        [new("source", "value", "target", "message")]);
}
