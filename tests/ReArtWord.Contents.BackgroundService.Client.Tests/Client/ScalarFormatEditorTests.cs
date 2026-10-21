using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>数値書式の接続型と、適用可能な変換だけに表示する入力欄を確認します。</summary>
public sealed class ScalarFormatEditorTests
{
    /// <summary>文字列・Pathの文字列化や数値への変換には未使用の書式欄を出しません。</summary>
    [Theory]
    [InlineData("integer", "string", true)]
    [InlineData("number", "string", true)]
    [InlineData("string", "string", false)]
    [InlineData("char", "string", false)]
    [InlineData("path", "string", false)]
    [InlineData("boolean", "string", false)]
    [InlineData("string", "integer", false)]
    public void OnlyApplicableConversionsOfferAnUnusedFormatInput(string sourceType, string targetType, bool visible)
    {
        WorkflowDefinition document = Document(sourceType, targetType);
        GraphTypeInference graph = new(document, Catalog());
        Assert.Equal(visible, graph.EditorDescriptor("convert")!.Inputs.Any(port => port.Name is "format"));
        Assert.Equal("string", graph.Port("convert", "format", false).InferredType);
    }

    /// <summary>書式の配線を型変更で隠さず、数値を誤って書式へ接続しません。</summary>
    /// <returns>接続・適用外設定への切替・Undoの確認です。</returns>
    [Fact]
    public async Task FormatWiresRemainEditableWhenConversionChangesAsync()
    {
        WorkflowDefinition document = Document("integer", "string");
        NodeDescriptor[] catalog = Catalog();
        EditorDocument model = new(new(new BrowserTestRuntime()));
        await model.LoadAsync(new(document, "base", DateTimeOffset.UtcNow, EActivationPolicy.Auto)).ConfigureAwait(true);
        Assert.False(NodeInputEditing.Connect(model, new("source", "value", "convert", "format"), catalog));
        Assert.True(NodeInputEditing.Connect(model, new("format", "value", "convert", "format"), catalog));
        model.ChangeNode("convert", node => node with { Settings = JsonSerializer.SerializeToElement(new { targetType = "integer" }) });
        GraphTypeInference graph = new(model.Draft.Document, catalog);
        Assert.Contains(graph.EditorDescriptor("convert")!.Inputs, port => port.Name is "format");
        Assert.Contains(model.Draft.Document.Wires, wire => wire.ToPort is "format");
        model.Undo();
        Assert.Equal("string", model.Draft.Document.Nodes[1].Settings.GetProperty("targetType").GetString());
    }

    /// <summary>値と書式の二つの接続元を配置します。</summary>
    /// <returns>未接続の書式入力を含む変換です。</returns>
    private static WorkflowDefinition Document(string sourceType, string targetType) => new("format", "Format", [
        new("source", "value.constant", JsonSerializer.SerializeToElement(new { type = sourceType, text = "42" })),
        new("convert", "data.convert", JsonSerializer.SerializeToElement(new { targetType })),
        new("format", "value.constant", JsonSerializer.SerializeToElement(new { type = "string", text = "D4" })),
    ], [new("source", "value", "convert", "value")]);

    /// <summary>既存スカラー変換と新しい書式入力の公開契約を用意します。</summary>
    /// <returns>編集画面が受け取るカタログです。</returns>
    private static NodeDescriptor[] Catalog()
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        return [
            new("value.constant", "Value", "Values", "", ENodeKind.Action, EExecutionLocation.Independent, [], [new("value")], ["success"], settings, CanEvaluateStatically: true),
            new("data.convert", "Convert", "Data", "", ENodeKind.Action, EExecutionLocation.Independent, [new("value", Required: true), new("format", EPortValueKind.Json)], [new("value", EPortValueKind.Json)], ["success"], settings),
        ];
    }
}
