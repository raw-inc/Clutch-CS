using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>HTTP応答全体から本文・ステータス・メタデータを分離します。</summary>
public sealed class HttpResponseParseNode : IRuntimeValueNode
{
    /// <summary>通信を再実行せず、受け渡された応答だけを解析します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("http.response.parse", "Parse HTTP response", "Communication", """
        {"type":"object","properties":{"format":{"type":"string","enum":["auto","json","text","binary"],"default":"auto","description":"Automatic uses Content-Type; unknown types remain Binary. For old responses without original bytes, choose Text or JSON. Binary requires a response captured as Binary or Automatic."}}}
        """, inputs: [new("value", EPortValueKind.Json, true) { DataType = new("object") }],
        outputs: [new("value"), new("statusCode", EPortValueKind.Json) { DataType = new("integer") }, new("contentType", EPortValueKind.Json) { DataType = new("string") },
            new("fileName", EPortValueKind.Json) { DataType = new("string") }, new("headers", EPortValueKind.Json) { DataType = new("object") }]) with { IsValueOperation = true };

    /// <summary>本文の解析方式を検証します。</summary>
    /// <returns>公開設定との不一致です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>応答の本文と周辺情報を別の出力へ取り出します。</summary>
    /// <returns>解析結果または不正な応答です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return ValueTask.FromResult(invalid);
        }
        try
        {
            return ValueTask.FromResult(HttpResponseValues.Parse(NodeSupport.Input(context) ?? NodeValue.Null, new NodeSettings(context.Settings).ReadText("format", "auto")!));
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return ValueTask.FromResult(NodeResult.Failure("http.response.parse.invalid", "Cannot parse this response in the selected format. Binary requires original bytes; Text/JSON requires valid text. No HTTP request was repeated."));
        }
    }
}
