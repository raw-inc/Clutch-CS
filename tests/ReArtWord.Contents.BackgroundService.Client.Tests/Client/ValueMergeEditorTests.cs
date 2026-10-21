using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>値の到着を束ねる可変入力をカードと詳細欄で表示します。</summary>
public sealed class ValueMergeEditorTests
{
    /// <summary>PC候補の型を維持し、値合流の入力数と実行PCへの接続を公開します。</summary>
    [Fact]
    public void PcCandidatesCanPassThroughVariableMergeInputs()
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        NodeDescriptor selector = new(PcSelectionPorts.SelectNode, "PC", "Execution", "", ENodeKind.Action, EExecutionLocation.Independent,
            [], [new("value", EPortValueKind.PcSelection)], ["success"], empty, CanEvaluateStatically: true);
        NodeDescriptor operation = new("test.action", "Run", "Control", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [], [], ["success"], empty);
        NodeDescriptor[] catalog = [Descriptor(), selector, operation];
        WorkflowDefinition document = new("pc-merge", "PC merge", [new("pc", selector.Type, empty), new("merge", ValueMergePorts.NodeType, empty), new("run", operation.Type, empty)],
            [new("pc", "value", "merge", "input1")]);
        Assert.True(GraphConnectionPolicy.CanConnect(document, catalog, new("merge", "value", "run", PcSelectionPorts.Execution)));
        GraphTypeInference inference = new(document, catalog);
        NodeDescriptor merge = inference.Descriptor("merge")!;
        Assert.False(merge.CanEvaluateStatically == true);
        Assert.Equal(EPortValueKind.PcSelection, merge.Outputs.Single(port => port.Name is "value").ValueKind);
        Assert.Equal(2, EditorNodeDescriptor.Editable(merge, document.Nodes[1], document)!.Inputs.Count);
        Assert.False(PcSelectionPorts.AllowsMultiple(merge, "input1"));
        Assert.Equal(PcSelectionPorts.TypeName, inference.Port("merge", "value", true).Key);
    }

    /// <summary>一入力から開始し、接続のたびに次を公開して既存番号を維持します。</summary>
    [Fact]
    public void ExpandsValuePortsFromConnections()
    {
        WorkflowDefinition document = Definition();
        Assert.Single(EditorNodeDescriptor.Resolve(Descriptor(), document.Nodes[0], document)!.Inputs);
        document = document with { Wires = [new("a", "value", "merge", "input1")] };
        Assert.Equal(2, EditorNodeDescriptor.Resolve(Descriptor(), document.Nodes[0], document)!.Inputs.Count);
        document = document with { Wires = [new("a", "value", "merge", "input1"), new("b", "value", "merge", "input2")] };
        NodeDescriptor resolved = EditorNodeDescriptor.Resolve(Descriptor(), document.Nodes[0], document)!;
        Assert.Equal(3, resolved.Inputs.Count);
        Assert.All(resolved.Inputs, port => Assert.Equal(EPortValueKind.Any, port.ValueKind));
        document = document with { Wires = [new("b", "value", "merge", "input4")] };
        Assert.Equal(Enumerable.Range(1, 4).Select(ConcatPorts.Name), EditorNodeDescriptor.Resolve(Descriptor(), document.Nodes[0], document)!.Inputs.Select(port => port.Name));
    }

    /// <summary>検索・配置前も可変口を絞り、固定入力と実行契約の64口を保持します。</summary>
    [Theory]
    [InlineData("control.valueMerge", 1)]
    [InlineData("control.signal.each", 2)]
    [InlineData("control.signal.first", 2)]
    [InlineData("control.signal.all", 2)]
    [InlineData("data.concat", 2)]
    [InlineData("osc.packet", 2)]
    public void PreviewsUseTheSameVariablePortRules(string type, int expected)
    {
        NodeDescriptor descriptor = Descriptor() with { Type = type, Inputs = [new("fixed"), .. Descriptor().Inputs] };
        IReadOnlyList<PortDescriptor> initial = EditorNodeDescriptor.VisibleInputs(descriptor, []);
        Assert.Equal(expected + 1, initial.Count);
        Assert.Equal("fixed", initial[0].Name);
        Assert.Equal(65, descriptor.Inputs.Count);
        IReadOnlyList<PortDescriptor> populated = EditorNodeDescriptor.VisibleInputs(descriptor, ["input1", "input2"]);
        Assert.Equal(4, populated.Count);
        Assert.Equal("input3", populated[^1].Name);
    }

    /// <summary>編集欄では値の接続方法を示し、定数を受信イベントとして設定させません。</summary>
    /// <returns>カードとインスペクターの実Razor表示です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RendersArrivalInputsWithoutConstantEditorsAsync(bool inline)
    {
        WorkflowDefinition document = Definition();
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(document, "saved", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        UiText ui = new(new(new BrowserTestRuntime()));
        await ui.ChangeAsync("en").ConfigureAwait(true);
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
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["Node"] = document.Nodes[0], ["Ports"] = Descriptor().Inputs.Take(2).ToArray(), ["Inline"] = inline };
            return (await renderer.RenderComponentAsync<NodeInputFields>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        }).ConfigureAwait(true);
        Assert.Contains("Connect a value output; each arrival starts its own downstream execution.", html, StringComparison.Ordinal);
        Assert.Contains("Input 1", html, StringComparison.Ordinal);
        Assert.Contains("Input 2", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<textarea", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", html, StringComparison.Ordinal);
        Assert.False(model.Draft.Dirty);
    }

    /// <summary>空の値合流カードです。</summary>
    /// <returns>変更前の文書です。</returns>
    private static WorkflowDefinition Definition() => new("merge-ui", "Merge UI", [new("merge", ValueMergePorts.NodeType, JsonSerializer.SerializeToElement(new { }))], []);

    /// <summary>UIが扱う契約上の最大入力数です。</summary>
    /// <returns>可変口を持つ公開記述です。</returns>
    private static NodeDescriptor Descriptor() => new(ValueMergePorts.NodeType, "Value merge", "Control", "", ENodeKind.Action, EExecutionLocation.Independent,
        Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ConcatPorts.Name(index))).ToArray(), [new("value"), new("start", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { }));
}
