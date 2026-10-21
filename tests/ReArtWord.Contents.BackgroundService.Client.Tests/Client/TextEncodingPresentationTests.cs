using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>バイト列と文字列の接続型、およびBOM設定の表示を確認します。</summary>
public sealed class TextEncodingPresentationTests
{
    /// <summary>復号は文字列、符号化はバイト列として表示し、相互に接続できます。</summary>
    [Fact]
    public void EncodedAndDecodedPortsRemainDistinct()
    {
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition document = new("text", "Text conversion", [new("decode", "text.decode", settings), new("encode", "text.encode", settings)], []);
        NodeDescriptor decode = new("text.decode", "Decode", "Data", "", ENodeKind.Action, EExecutionLocation.Independent,
            [new("value", EPortValueKind.Binary, true) { DataType = new("binary") }], [new("value", EPortValueKind.Json) { DataType = new("string") }], ["success"], settings);
        NodeDescriptor encode = new("text.encode", "Encode", "Data", "", ENodeKind.Action, EExecutionLocation.Independent,
            [new("value", EPortValueKind.Json, true) { DataType = new("string") }], [new("value", EPortValueKind.Binary) { DataType = new("binary") }], ["success"], settings);
        GraphTypeInference graph = new(document, [decode, encode]);
        Assert.Equal("string", graph.Port("decode", "value", true).InferredType);
        Assert.Equal("binary", graph.Port("encode", "value", true).InferredType);
        Assert.Equal("binary", graph.Port("decode", "value", false).InferredType);
        Assert.Equal("string", graph.Port("encode", "value", false).InferredType);
        Assert.True(GraphConnectionPolicy.CanConnect(document, [decode, encode], new("decode", "value", "encode", "value")));
        Assert.True(GraphConnectionPolicy.CanConnect(document, [decode, encode], new("encode", "value", "decode", "value")));
    }

    /// <summary>方向ごとに異なる自動BOMと既定文字コードの説明を表示します。</summary>
    /// <returns>英語と日本語の表示確認です。</returns>
    [Fact]
    public async Task EncodingAndBomChoicesHaveLocalizedMeaningsAsync()
    {
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("Text encoding", text.SettingName("encoding"));
        Assert.Equal("Automatic (BOM, otherwise UTF-8)", text.SettingChoiceName("text.decode", "encoding", "auto"));
        Assert.Equal("Automatic (BOM for UTF-16/32 only)", text.SettingChoiceName("text.encode", "bom", "auto"));
        await text.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("文字コード", text.SettingName("encoding"));
        Assert.Equal("なしとして読む（先頭を除去しない）", text.SettingChoiceName("text.decode", "bom", "none"));
        Assert.Equal("なし（BOMを付与しない）", text.SettingChoiceName("text.encode", "bom", "none"));
    }
}
