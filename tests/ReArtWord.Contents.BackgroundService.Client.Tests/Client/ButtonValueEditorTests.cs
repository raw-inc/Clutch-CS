using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>ボタンの入出力が一組ずつ増え、検索・配置後の表示を揃えます。</summary>
public sealed class ButtonValueEditorTests
{
    private static readonly string[] initialInputs = ["value", "label"];
    private static readonly string[] initialOutputs = ["value", "start"];

    /// <summary>PC候補の接続・表示・実行PCへの配線を、可変値ポートごとに維持します。</summary>
    /// <returns>通常値との共存、候補の置換とUndoを含む実編集操作です。</returns>
    [Theory]
    [InlineData("value")]
    [InlineData("value2")]
    [InlineData("value64")]
    public async Task PcCandidatesPassThroughEachValuePairAsync(string port)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NodeDescriptor selector = new(PcSelectionPorts.SelectNode, "PC", "Execution", "", ENodeKind.Action, EExecutionLocation.Independent,
            [], [new("value", EPortValueKind.PcSelection)], ["success"], empty, CanEvaluateStatically: true);
        NodeDescriptor operation = new("test.action", "Run", "Control", "", ENodeKind.Action, EExecutionLocation.Independent, [new("value")], [], ["success"], empty);
        NodeDescriptor constant = new("value.constant", "Text", "Values", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value")], ["success"], empty, CanEvaluateStatically: true);
        NodeDescriptor[] catalog = [Descriptor(), selector, operation, constant];
        string payload = port switch { "value" => "value2", _ => "value" };
        NodeDefinition button = new("button", ButtonValuePorts.NodeType, empty, new Dictionary<string, NodeValue> { [payload] = NodeValue.FromObject("payload") });
        WorkflowDefinition document = new("button-pc", "Button PC", [button, new("pc", selector.Type, empty), new("other", selector.Type, empty), new("run", operation.Type, empty),
            new("text", constant.Type, JsonSerializer.SerializeToElement(new { type = "string", text = "host-a" }))], []);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(document, "saved", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        Assert.True(NodeInputEditing.Connect(model, new("pc", "value", "button", port), catalog));
        GraphTypeInference graph = new(model.Draft.Document, catalog);
        Assert.Equal(PcSelectionPorts.TypeName, graph.Port("button", port, false).Key);
        Assert.Equal(PcSelectionPorts.TypeName, graph.Port("button", port, true).Key);
        Assert.Equal("string", graph.Port("button", payload, true).Key);
        Assert.Contains(graph.EditorDescriptor("button")!.Inputs, input => string.Equals(input.Name, port, StringComparison.Ordinal));
        WireDefinition execution = new("button", port, "run", PcSelectionPorts.Execution);
        Assert.True(NodeInputEditing.Connect(model, execution, catalog));
        Assert.Equal("pc", PcSelectionPorts.Origin(model.Draft.Document, execution)!.Id);
        Assert.True(NodeInputEditing.Connect(model, new("other", "value", "button", "value3"), catalog));
        Assert.Equal("pc", PcSelectionPorts.Origin(model.Draft.Document, execution)!.Id);
        Assert.False(NodeInputEditing.Connect(model, new("text", "value", "button", port), catalog));
        Assert.False(NodeInputEditing.Connect(model, new("pc", "value", "button", "label"), catalog));
        Assert.False(NodeInputEditing.Connect(model, new("pc", "value", "button", "value65"), catalog));
        Assert.False(NodeInputEditing.Connect(model, new("pc", "value", "button", port, Path: "/0"), catalog));
        Assert.False(NodeInputEditing.Connect(model, new("button", port, "run", "value"), catalog));
        Assert.True(NodeInputEditing.Connect(model, new("other", "value", "button", port), catalog));
        Assert.Single(model.Draft.Document.Wires, wire => wire.ToNode is "button" && string.Equals(wire.ToPort, port, StringComparison.Ordinal));
        Assert.Equal("other", PcSelectionPorts.Origin(model.Draft.Document, execution)!.Id);
        model.Undo();
        Assert.Equal("pc", PcSelectionPorts.Origin(model.Draft.Document, execution)!.Id);
    }

    /// <summary>入力や配線で追加し、途中を消しても後続の値番号を移動しません。</summary>
    [Fact]
    public void GrowsPairsFromValuesAndConnectionsWithoutRenumbering()
    {
        NodeDefinition node = new("button", ButtonValuePorts.NodeType, JsonSerializer.SerializeToElement(new { }));
        WorkflowDefinition document = new("buttons", "Buttons", [node], []);
        AssertPairs(document, 1);
        node = node with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(false) } };
        document = document with { Nodes = [node] };
        AssertPairs(document, 2);
        document = document with { Wires = [new("constant", "value", "button", "value2")] };
        AssertPairs(document, 3);
        node = node with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(false), ["value3"] = NodeValue.Null } };
        document = document with { Nodes = [node] };
        AssertPairs(document, 4);
        document = document with { Wires = [] };
        AssertPairs(document, 3);
        node = node with { Inputs = null };
        document = document with { Nodes = [node], Wires = [new("button", "value3/number", "target", "value")] };
        NodeDescriptor descriptor = AssertPairs(document, 3);
        Assert.Contains(descriptor.Outputs, port => port.Name is "value3/number");
        document = document with { Wires = [] };
        AssertPairs(document, 1);
    }

    /// <summary>初期プレビューは一組に絞り、ラベルやSignalは可変数に数えません。</summary>
    [Fact]
    public void PreviewsKeepFixedPortsAndLimitPairs()
    {
        NodeDescriptor descriptor = Descriptor();
        Assert.Equal(initialInputs, EditorNodeDescriptor.VisibleInputs(descriptor, ["label"]).Select(port => port.Name));
        Assert.Equal(initialOutputs, EditorNodeDescriptor.VisibleOutputs(descriptor, [], ["start"]).Select(port => port.Name));
        string[] inputs = Enumerable.Range(1, ConcatPorts.Maximum).Select(ButtonValuePorts.Name).ToArray();
        Assert.Equal(ConcatPorts.Maximum + 1, EditorNodeDescriptor.VisibleInputs(descriptor, inputs).Count);
        Assert.Equal(ConcatPorts.Maximum + 1, EditorNodeDescriptor.VisibleOutputs(descriptor, inputs).Count);
        Assert.Equal(ConcatPorts.Maximum + 1, descriptor.Inputs.Count);
        Assert.Equal(ConcatPorts.Maximum + 1, descriptor.Outputs.Count);
    }

    /// <summary>カードと詳細欄の双方で番号付きの値を直接編集できるようにします。</summary>
    /// <returns>日本語と英語の実Razor入力欄です。</returns>
    [Theory]
    [InlineData(false, "ja", "押下時に渡す値 2")]
    [InlineData(true, "ja", "押下時に渡す値 2")]
    [InlineData(false, "en", "Value on press 2")]
    [InlineData(true, "en", "Value on press 2")]
    public async Task RendersNumberedEditableValuesAsync(bool inline, string language, string caption)
    {
        NodeDefinition node = new("button", ButtonValuePorts.NodeType, JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(42) });
        WorkflowDefinition document = new("buttons", "Buttons", [node], []);
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(document, "saved", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        UiText ui = new(new(new BrowserTestRuntime()));
        await ui.ChangeAsync(language).ConfigureAwait(true);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(model);
        services.AddSingleton<EditorPresentationCache>();
        services.AddSingleton(ui);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["Node"] = node, ["Ports"] = EditorNodeDescriptor.Resolve(Descriptor(), node, document)!.Inputs, ["Inline"] = inline };
            return (await renderer.RenderComponentAsync<NodeInputFields>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Contains(caption, System.Net.WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Contains("<textarea", html, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", html, StringComparison.Ordinal);
        string expectedOutput = language switch { "ja" => "押下時の値 2", _ => "Pressed value 2" };
        Assert.Equal(expectedOutput, ui.PortName(ButtonValuePorts.NodeType, "value2", true));
        Assert.False(model.Draft.Dirty);
    }

    /// <summary>固定口を除いて入力と出力の番号が一致することを検査します。</summary>
    /// <returns>プロパティ配線の確認にも使う表示記述です。</returns>
    private static NodeDescriptor AssertPairs(WorkflowDefinition document, int count)
    {
        NodeDescriptor descriptor = EditorNodeDescriptor.Resolve(Descriptor(), document.Nodes[0], document)!;
        string[] expected = Enumerable.Range(1, count).Select(ButtonValuePorts.Name).ToArray();
        Assert.Equal(expected, descriptor.Inputs.Where(port => 0 < ButtonValuePorts.Index(port.Name)).Select(port => port.Name));
        Assert.Equal(expected, descriptor.Outputs.Where(port => 0 < ButtonValuePorts.Index(port.Name)).Select(port => port.Name));
        Assert.Contains(descriptor.Inputs, port => port.Name is "label");
        Assert.Contains(descriptor.Outputs, port => port.Name is "start");
        return descriptor;
    }

    /// <summary>公開される最大口数を、保存契約と同じ識別子で用意します。</summary>
    /// <returns>表示専用のボタン契約です。</returns>
    private static NodeDescriptor Descriptor() => new(ButtonValuePorts.NodeType, "Button", "Control", "", ENodeKind.Action, EExecutionLocation.Independent,
        [.. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ButtonValuePorts.Name(index))), new("label")],
        [.. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ButtonValuePorts.Name(index))), new("start", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
