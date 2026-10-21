using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>Endの可変結果・制御信号・出力別型推測を、保存済みのポート名を変えずに表示します。</summary>
public sealed class IterationResultEditorTests
{
    /// <summary>値を入力すると一組増え、途中の空欄や出力だけの接続も詰めません。</summary>
    [Fact]
    public void ResultPairsGrowAndPreserveExistingConnections()
    {
        NodeDefinition end = Node("end", IterationNodeTypes.End);
        WorkflowDefinition document = new("results", "Results", [end], []);
        AssertPairs(document, 1);
        end = end with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(42) } };
        document = document with { Nodes = [end] };
        AssertPairs(document, 2);
        document = document with { Wires = [new("body", "value", "end", "value2")] };
        AssertPairs(document, 3);
        end = end with { Inputs = null };
        document = document with { Nodes = [end], Wires = [new("end", "value5", "sink", "value", Path: "/0/field")] };
        NodeDescriptor visible = AssertPairs(document, 5);
        Assert.Contains(visible.Outputs, port => port.Name is "value5");
        Assert.Equal("/0/field", Assert.Single(document.Wires).Path);
        document = document with { Wires = [] };
        AssertPairs(document, 1);
    }

    /// <summary>各結果入力の型をそれぞれ対応する配列型へ引き継ぎます。</summary>
    [Fact]
    public void EachResultColumnKeepsItsOwnElementType()
    {
        NodeDefinition number = Node("number", "value.constant") with { Settings = JsonSerializer.SerializeToElement(new { type = "integer", text = "12" }) };
        NodeDefinition text = Node("text", "value.constant") with { Settings = JsonSerializer.SerializeToElement(new { type = "string", text = "item" }) };
        NodeDefinition end = Node("end", IterationNodeTypes.End) with { Inputs = new Dictionary<string, NodeValue> { ["value3"] = NodeValue.FromObject(true) } };
        WorkflowDefinition document = new("types", "Types", [number, text, end], [new("number", "value", "end", "value"), new("text", "value", "end", "value2")]);
        NodeDescriptor constant = new("value.constant", "Value", "Values", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value")], ["success"], JsonSerializer.SerializeToElement(new { }));
        WorkflowTypeInference inference = new(document, [constant, Descriptor()]);
        Assert.Equal("integer", inference.PortType("end", "value", true)?.ElementType?.Type);
        Assert.Equal("string", inference.PortType("end", "value2", true)?.ElementType?.Type);
        Assert.Equal("boolean", inference.PortType("end", "value3", true)?.ElementType?.Type);
        Assert.Equal("array", inference.PortType("end", "value2", true)?.Type);
    }

    /// <summary>制御口は通常データやPointer抽出と区別して、実際の開始・完了信号だけを受けます。</summary>
    [Theory]
    [InlineData("continue")]
    [InlineData("break")]
    public void ControlInputsRequireSignalOutputs(string port)
    {
        NodeDefinition source = Node("source", "test.async"), end = Node("end", IterationNodeTypes.End);
        WorkflowDefinition document = new("signals", "Signals", [source, end], []);
        NodeDescriptor sender = new("test.async", "Action", "Control", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value", EPortValueKind.Json)], ["success"], JsonSerializer.SerializeToElement(new { })) { HasAsyncCompletion = true };
        NodeDescriptor[] catalog = [sender, Descriptor()];
        WireDefinition valid = new("source", CompletionPorts.Signal, "end", port);
        Assert.True(WorkflowConnectionTypes.CanConnect(document, catalog, valid));
        Assert.False(WorkflowConnectionTypes.CanConnect(document, catalog, valid with { FromPort = "value" }));
        Assert.False(WorkflowConnectionTypes.CanConnect(document, catalog, valid with { Path = string.Empty }));
        Assert.Equal("port.type.trigger", new GraphTypeInference(document, catalog).Port("end", port, false).Label);
    }

    /// <summary>制御口は可変数へ数えず、末尾の上限も既存の可変ポートと揃えます。</summary>
    [Fact]
    public void FixedControlPortsDoNotCreateExtraPairs()
    {
        NodeDescriptor descriptor = Descriptor();
        string[] control = ["loop", "continue", "break"];
        Assert.Equal(["loop", "value", "continue", "break"], EditorNodeDescriptor.VisibleInputs(descriptor, control).Select(port => port.Name));
        string[] supplied = Enumerable.Range(1, ConcatPorts.Maximum).Select(ValuePortPairs.Name).ToArray();
        Assert.Equal(ConcatPorts.Maximum + 3, EditorNodeDescriptor.VisibleInputs(descriptor, supplied).Count);
        Assert.Equal(ConcatPorts.Maximum, EditorNodeDescriptor.VisibleOutputs(descriptor, supplied).Count);
    }

    /// <summary>追加された口の入出力ラベルを両言語で区別します。</summary>
    /// <returns>番号付き表示の確認です。</returns>
    [Theory]
    [InlineData("ja", "今回の処理結果 2", "まとめた処理結果 2")]
    [InlineData("en", "Item result 2", "Collected results 2")]
    public async Task ResultLabelsDistinguishInputAndOutputAsync(string language, string input, string output)
    {
        UiText ui = new(new(new BrowserTestRuntime()));
        await ui.ChangeAsync(language).ConfigureAwait(true);
        Assert.Equal(input, ui.PortName(IterationNodeTypes.End, "value2", false));
        Assert.Equal(output, ui.PortName(IterationNodeTypes.End, "value2", true));
        Assert.Contains("continue", ui.PortName(IterationNodeTypes.End, "continue", false), StringComparison.Ordinal);
        Assert.Contains("break", ui.PortName(IterationNodeTypes.End, "break", false), StringComparison.Ordinal);
    }

    /// <summary>使用数と共通の制御口が両側で維持されることを検査します。</summary>
    /// <returns>プロパティ出力を確認するための表示契約です。</returns>
    private static NodeDescriptor AssertPairs(WorkflowDefinition document, int count)
    {
        NodeDescriptor visible = EditorNodeDescriptor.Resolve(Descriptor(), document.Nodes[0], document)!;
        string[] expected = Enumerable.Range(1, count).Select(ValuePortPairs.Name).ToArray();
        Assert.Equal(expected, visible.Inputs.Where(port => 0 < ValuePortPairs.Index(port.Name)).Select(port => port.Name));
        Assert.Equal(expected, visible.Outputs.Where(port => 0 < ValuePortPairs.Index(port.Name)).Select(port => port.Name));
        Assert.Contains(visible.Inputs, port => port.Name is "continue");
        Assert.Contains(visible.Inputs, port => port.Name is "break");
        return visible;
    }

    /// <summary>実行機能を持たない最小の保存定義です。</summary>
    /// <returns>指定した種類のノードです。</returns>
    private static NodeDefinition Node(string id, string type) => new(id, type, JsonSerializer.SerializeToElement(new { }));

    /// <summary>可変口と制御口を含む公開契約を表示試験へ用意します。</summary>
    /// <returns>Endの固定したポート契約です。</returns>
    private static NodeDescriptor Descriptor() => new(IterationNodeTypes.End, "End", "Control", "", ENodeKind.Action, EExecutionLocation.Independent,
        [new("loop", EPortValueKind.Json), .. Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ValuePortPairs.Name(index), EPortValueKind.Json)),
            new("continue", EPortValueKind.Json) { DataType = new("boolean", Optional: true) }, new("break", EPortValueKind.Json) { DataType = new("boolean", Optional: true) }],
        Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ValuePortPairs.Name(index), EPortValueKind.Json)).ToArray(), ["success"], JsonSerializer.SerializeToElement(new { }));
}
