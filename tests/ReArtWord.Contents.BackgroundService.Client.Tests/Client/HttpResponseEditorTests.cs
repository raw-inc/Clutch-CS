using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>HTTP応答本文の形式と、画像保存へ使う出力を画面上で区別します。</summary>
public sealed class HttpResponseEditorTests
{
    /// <summary>設定の変更後にBinaryをJSONやテキストと表示しません。</summary>
    [Theory]
    [InlineData("legacy", "string")]
    [InlineData("text", "string")]
    [InlineData("binary", "binary")]
    [InlineData("json", "any")]
    [InlineData("auto", "any")]
    public void ResponseFormatDeterminesTheBodyPortType(string format, string expected)
    {
        NodeDefinition node = new("http", "http.request", JsonSerializer.SerializeToElement(new { responseBodyFormat = format }));
        NodeDescriptor descriptor = new("http.request", "HTTP", "Communication", "", ENodeKind.Action, EExecutionLocation.ExplicitPeer, [],
            [new("value"), new("body"), new("statusCode", EPortValueKind.Json) { DataType = new("integer") }], ["success"], JsonSerializer.SerializeToElement(new { }));
        GraphTypeInference graph = new(new("flow", "HTTP", [node], []), [descriptor]);
        Assert.Equal(expected, graph.Port("http", "body", true).Key);
        Assert.Equal("object", graph.Port("http", "value", true).InferredType);
        Assert.Equal("integer", graph.Port("http", "statusCode", true).InferredType);
    }

    /// <summary>保存用バイトと応答情報を両言語で区別できます。</summary>
    /// <returns>設定とポートのラベルです。</returns>
    [Fact]
    public async Task BinaryAndMetadataLabelsAreLocalizedAsync()
    {
        UiText text = new(new(new BrowserTestRuntime()));
        await text.InitializeAsync().ConfigureAwait(true);
        Assert.Equal("Binary (original bytes)", text.SettingChoiceName("http.request", "responseBodyFormat", "binary"));
        Assert.Equal("Response body", text.PortName("http.request", "body", true));
        await text.ChangeAsync("ja").ConfigureAwait(true);
        Assert.Equal("応答本文の形式", text.SettingName("responseBodyFormat"));
        Assert.Equal("解析した本文", text.PortName("http.response.parse", "value", true));
        Assert.Equal("復号したバイト列", text.PortName("data.base64.decode", "value", true));
    }
}
