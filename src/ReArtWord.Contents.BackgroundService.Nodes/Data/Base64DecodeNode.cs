using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>JSONのBase64文字列やdata URLを、保存できるバイト列へ変換します。</summary>
public sealed class Base64DecodeNode : IRuntimeValueNode
{
    /// <summary>Base64入力と、Binary・任意MIMEを出力として公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.base64.decode", "Decode Base64", "Data", """
        {"type":"object","properties":{"maxBytes":{"type":"integer","minimum":1,"maximum":16777216,"default":16777216}}}
        """, inputs: [new("value", EPortValueKind.Json, true) { DataType = new("string") }],
        outputs: [new("value", EPortValueKind.Binary) { DataType = new("binary") }, new("contentType", EPortValueKind.Json) { DataType = new("string") }]) with { IsValueOperation = true };

    /// <summary>復号後の上限を検証します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>標準Base64とbase64形式のdata URLを復号します。</summary>
    /// <returns>元バイト列と、data URLにある場合のContent-Typeです。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return ValueTask.FromResult(invalid);
        }
        if (NodeSupport.Input(context)?.GetString() is not { } text)
        {
            return ValueTask.FromResult(NodeResult.Failure("base64.input.text", "Supply a Base64 string or a base64 data URL."));
        }
        try
        {
            (byte[] bytes, string contentType) = Base64Content.Decode(text, new NodeSettings(context.Settings).ReadInt32("maxBytes", 16777216));
            return ValueTask.FromResult(NodeResult.Success() with { Outputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromBytes(bytes), ["contentType"] = NodeValue.FromObject(contentType) } });
        }
        catch (FormatException)
        {
            return ValueTask.FromResult(NodeResult.Failure("base64.invalid", "The Base64 text or data URL is invalid, or exceeds the decoded byte limit."));
        }
    }
}
