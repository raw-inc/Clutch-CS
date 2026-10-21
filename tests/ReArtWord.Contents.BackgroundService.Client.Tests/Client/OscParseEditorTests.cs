using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>OSC解析の型付き出力と受信共通の設定欄を確認します。</summary>
public sealed class OscParseEditorTests
{
    /// <summary>順序付きの型から、解析結果の出力だけを追加します。</summary>
    [Fact]
    public void ParserExposesReceiverArgumentTypesAsOutputs()
    {
        NodeDefinition parse = Definition();
        WorkflowDefinition document = new("flow", "Flow", [parse], []);
        NodeDescriptor descriptor = Descriptor();
        NodeDescriptor resolved = OscPorts.Resolve(descriptor, parse)!;
        Assert.Single(resolved.Inputs);
        Assert.Equal(["value", "address", "arguments", "argument1", "argument2", "argument3", "argument4"], resolved.Outputs.Select(port => port.Name));
        GraphTypeInference inference = new(document, [descriptor]);
        Assert.Equal("integer", inference.Port(parse.Id, "argument1", true).Key);
        Assert.Equal("string", inference.Port(parse.Id, "argument2", true).Key);
        Assert.Equal("binary", inference.Port(parse.Id, "argument3", true).Key);
        Assert.Equal("boolean", inference.Port(parse.Id, "argument4", true).Key);
    }

    /// <summary>Objectとバイト列を接続でき、既知の文字列・数値は拒否します。</summary>
    [Theory]
    [InlineData("object", true)]
    [InlineData("binary", true)]
    [InlineData("string", false)]
    [InlineData("integer", false)]
    public void ParserInputHasTheSameContractInTheEditor(string type, bool allowed)
    {
        NodeDefinition source = new("source", "test.source", JsonSerializer.Deserialize<JsonElement>("{}"));
        NodeDefinition parse = Definition();
        NodeDescriptor origin = Descriptor() with { Type = source.Type, Inputs = [], Outputs = [new("value") { DataType = new(type) }] };
        WorkflowDefinition document = new("flow", "Flow", [source, parse], []);
        Assert.Equal(allowed, WorkflowConnectionTypes.CanConnect(document, [origin, Descriptor()], new(source.Id, "value", parse.Id, "value")));
    }

    /// <summary>JSON入力欄ではなく、受信ノードと共通の引数型選択を表示します。</summary>
    /// <returns>日本語表示と未変更草稿の確認です。</returns>
    [Theory]
    [InlineData("{\"argumentTypes\":[\"i\",\"s\",\"b\",\"boolean\"]}", 4)]
    [InlineData("{\"argumentTypes\":{\"type\":\"integer\"}}", 1)]
    [InlineData("{\"argumentTypes\":[{\"type\":\"integer\"},{\"type\":\"boolean\"}]}", 2)]
    public async Task ParserRendersTheReceiverArgumentEditorAsync(string settings, int count)
    {
        BrowserStorage storage = new(new BrowserTestRuntime());
        EditorDocument model = new(storage);
        NodeDefinition parse = Definition() with { Settings = JsonSerializer.Deserialize<JsonElement>(settings) };
        model.Import(new("flow", "Flow", [parse], []));
        UiText ui = new(storage);
        await ui.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("OSCパケットを解析", ui.NodeName(Descriptor()));
        Assert.Equal("引数 1", ui.PortName(parse.Type, "argument1", true));
        using HttpClient client = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new BrowserTestRuntime());
        services.AddSingleton(model);
        services.AddSingleton(ui);
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(new SettingChoiceProvider(new ManagementApi(client)));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { [nameof(SchemaFields.NodeId)] = parse.Id, [nameof(SchemaFields.Settings)] = parse.Settings, [nameof(SchemaFields.Schema)] = Descriptor().SettingsSchema };
            return (await renderer.RenderComponentAsync<SchemaFields>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Contains(ui.Format("OSC arguments ({0})", count), System.Net.WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.DoesNotContain("<textarea", html, StringComparison.Ordinal);
    }

    /// <summary>型の順序を持つ解析配置です。</summary>
    /// <returns>保存される設定です。</returns>
    private static NodeDefinition Definition() => new("parse", OscPorts.ParseNodeType, JsonSerializer.Deserialize<JsonElement>("""{"argumentTypes":["i","s","b","boolean"]}"""));

    /// <summary>エディタへ渡される解析ノードの公開部分です。</summary>
    /// <returns>動的出力の追加前の宣言です。</returns>
    private static NodeDescriptor Descriptor() => new(OscPorts.ParseNodeType, "Parse OSC packet", "Communication", "", ENodeKind.Action, EExecutionLocation.Independent,
        [new("value", Required: true)], [new("value"), new("address"), new("arguments")], ["success"], JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"argumentTypes":{"type":["array","object"],"items":{"type":["string","object"]}}}}"""));
}
