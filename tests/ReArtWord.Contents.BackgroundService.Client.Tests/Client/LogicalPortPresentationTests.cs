using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>値の種類、ノードの役割、保存用識別子を分けた日英表示を検証します。</summary>
public sealed class LogicalPortPresentationTests
{
    /// <summary>推測した型を格納形式に付け足さず、同じ意味の表示名と色を使います。</summary>
    /// <returns>言語変更後も値の種類と識別子が変わらない確認です。</returns>
    [Theory]
    [InlineData("42", "integer", "Integer", "整数")]
    [InlineData("1.25", "number", "Number", "数値")]
    [InlineData("true", "boolean", "Boolean", "真偽値")]
    [InlineData("hello", "string", "Text", "文字列")]
    [InlineData("[1,2]", "array", "Array of Integer", "整数の配列")]
    [InlineData("{}", "object", "Object", "オブジェクト")]
    public async Task InferredTypesUseLogicalLabelsAndFamilies(string text, string family, string english, string japanese)
    {
        NodeDefinition node = new("constant", "value.constant", JsonSerializer.SerializeToElement(new { text }));
        NodeDescriptor descriptor = Descriptor(node.Type, [new("value", EPortValueKind.Json)]);
        GraphTypeInference inference = new(new("workflow", "workflow", [node], []), [descriptor]);
        GraphPortPresentation port = inference.Port(node.Id, "value", true);
        UiText ui = new(new(new BrowserTestRuntime()));
        Assert.Equal(english, port.DisplayLabel(ui));
        Assert.Equal(family, port.Key);
        Assert.Equal("value", port.Name);
        Assert.Equal(EPortValueKind.Json, descriptor.Outputs[0].ValueKind);
        await ui.ChangeAsync("ja");
        Assert.Equal(japanese, port.DisplayLabel(ui));
        Assert.Equal(family, port.Key);
    }

    /// <summary>任意型、nullを許す型、実行結果の役割を取り違えません。</summary>
    /// <returns>内部形式や任意型を具体値と断定しない表示です。</returns>
    [Fact]
    public async Task UnknownOptionalAndRuntimeResultsRemainDistinct()
    {
        UiText ui = new(new(new BrowserTestRuntime()));
        await ui.ChangeAsync("ja");
        NodeDescriptor descriptor = Descriptor("control.condition", [new("value"), new("result")]);
        Assert.Equal("任意の値", GraphPresentation.Port(descriptor, "value", true).DisplayLabel(ui));
        Assert.Equal("実行結果", GraphPresentation.Port(descriptor, "result", true).DisplayLabel(ui));
        Assert.Equal("result", GraphPresentation.Port(descriptor, "result", true).Name);
        GraphPortPresentation optional = new("value", "string", "port.type.string", "", new ValueTypeDefinition("string", Optional: true));
        Assert.Equal("文字列（省略・null可）", optional.DisplayLabel(ui));
        Assert.Equal("未確定", GraphPresentation.Port(descriptor, "extension", true).DisplayLabel(ui));
    }

    /// <summary>同じvalue識別子でも処理の役割を示し、拡張側の名前は保持します。</summary>
    /// <returns>画面用の名前だけを言語に合わせる確認です。</returns>
    [Fact]
    public async Task PortNamesDescribeTheirNodeWithoutChangingIdentifiers()
    {
        UiText ui = new(new(new BrowserTestRuntime()));
        Assert.Equal("File contents", ui.PortName("file.read", "value", true));
        Assert.Equal("Reply data", ui.PortName("http.reply", "value", false));
        Assert.Equal("value", ui.PortName("extension.private", "value", true));
        Assert.Equal("Text", ui.SettingChoiceName("value.constant", "type", "string"));
        await ui.ChangeAsync("ja");
        Assert.Equal("読み取った内容", ui.PortName("file.read", "value", true));
        Assert.Equal("返信するデータ", ui.PortName("http.reply", "value", false));
        Assert.Equal("計算結果", ui.PortName("math.add", "value", true));
        Assert.Equal("実行結果", ui.PortName("control.condition", "result", true));
        Assert.Equal("実行PC", ui.PortName("control.pass", "$execution.pc", false));
        Assert.Equal("任意の値", ui.SettingChoiceName("value.type", "type", "any"));
        Assert.Equal("value", ui.PortName("extension.private", "value", true));
    }

    /// <summary>文字列の設定ポートはJSONではなく、受け取る値の種類で表示します。</summary>
    [Fact]
    public void SettingPortsUseTheirDeclaredValueType()
    {
        NodeDefinition node = new("constant", "value.constant", JsonSerializer.SerializeToElement(new { text = "value" }));
        NodeDescriptor descriptor = Descriptor(node.Type, [new("value")]) with { SettingsSchema = JsonSerializer.SerializeToElement(new { properties = new { text = new { type = "string" } } }) };
        GraphTypeInference inference = new(new("workflow", "workflow", [node], []), [descriptor]);
        GraphPortPresentation input = inference.Port(node.Id, "$settings.text", false);
        Assert.Equal("string", input.Key);
        Assert.Equal("Text", input.DisplayLabel(new(new(new BrowserTestRuntime()))));
    }

    /// <summary>表示の検証に必要な最小のノード契約です。</summary>
    /// <returns>指定した型と出力を持つ契約です。</returns>
    private static NodeDescriptor Descriptor(string type, IReadOnlyList<PortDescriptor> outputs) => new(type, type, "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [], outputs, [], JsonSerializer.SerializeToElement(new { }));
}
