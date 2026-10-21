using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>ファイルや通信のバイト列を、明示した文字コードとBOM方針で文字列へ変換します。</summary>
public sealed class TextDecodeNode : IRuntimeValueNode
{
    /// <summary>バイト列入力と文字列出力、文字コードとBOMの独立した選択を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("text.decode", "Decode bytes as text", "Data",
        TextEncodings.Schema(false),
        inputs: [new("value", EPortValueKind.Binary, true) { DataType = new("binary") }],
        outputs: [new("value", EPortValueKind.Json) { DataType = new("string") }]) with
        {
            IsValueOperation = true,
            Description = "Decode bytes from a file or network message as text using the selected encoding and BOM policy. Invalid bytes fail without replacement characters."
        };

    /// <summary>文字コードとBOM方針の未対応値を開始前に拒否します。</summary>
    /// <returns>公開設定の検証結果です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>BOMだけを必要に応じて除去し、改行・空白・本文中のU+FEFFを保持します。</summary>
    /// <returns>文字列または明示した変換失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return ValueTask.FromResult(invalid);
        }
        NodeValue? input = NodeSupport.Input(context);
        if (input is null)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.decode.input.missing", "Connect file contents or another byte array to the input.", EOutcome.NotExecuted));
        }
        if (input.Kind != EValueKind.Binary)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.decode.input.type", "The input must be bytes, not a file path or an already decoded value."));
        }
        NodeSettings settings = new(context.Settings);
        string requested = settings.ReadText("encoding") ?? "auto", bom = settings.ReadText("bom") ?? "auto";
        try
        {
            string automatic = "auto", present = "present", none = "none";
            Encoding? selected = null;
            if (!string.Equals(requested, automatic, StringComparison.Ordinal))
            {
                selected = TextEncodings.Resolve(requested);
            }
            Encoding? detected = null;
            if (!string.Equals(bom, none, StringComparison.Ordinal))
            {
                detected = TextEncodings.DetectBom(input.Binary.Span, selected);
            }
            if (detected is null && string.Equals(bom, present, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(NodeResult.Failure("text.decode.bom.missing", "A Unicode BOM is required but was not found."));
            }
            if (selected is not null && detected is not null && selected.CodePage != detected.CodePage)
            {
                return ValueTask.FromResult(NodeResult.Failure("text.decode.bom.conflict", "The BOM does not match the selected encoding."));
            }
            Encoding encoding = selected ?? detected ?? TextEncodings.Utf8;
            int offset = 0;
            if (detected is not null)
            {
                offset = detected.Preamble.Length;
            }
            string text = encoding.GetString(input.Binary.Span[offset..]);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(text)));
        }
        catch (DecoderFallbackException)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.decode.bytes.invalid", "The bytes are invalid for the selected encoding. Check the encoding or whether the file is truncated."));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.decode.encoding.unavailable", "The selected encoding is unavailable on this runtime."));
        }
    }
}
