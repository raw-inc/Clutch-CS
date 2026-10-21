using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>文字列を指定文字コードのバイト列へ変換し、必要なBOMだけを付けます。</summary>
public sealed class TextEncodeNode : IRuntimeValueNode
{
    /// <summary>文字列入力とバイト列出力、復号と共通の設定を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("text.encode", "Encode text as bytes", "Data", TextEncodings.Schema(true),
        inputs: [new("value", EPortValueKind.Json, true) { DataType = new("string") }],
        outputs: [new("value", EPortValueKind.Binary) { DataType = new("binary") }]) with
        {
            IsValueOperation = true,
            Description = "Encode text as bytes for a file or network message. Choose the encoding and BOM; unrepresentable characters fail without replacement."
        };

    /// <summary>対応文字コードとBOM方針を開始前に検証します。</summary>
    /// <returns>公開設定の検証結果です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>改行・空白を保持し、文字を代替文字へ置き換えずに符号化します。</summary>
    /// <returns>符号化されたバイト列または明示した変換失敗です。</returns>
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
            return ValueTask.FromResult(NodeResult.Failure("text.encode.input.missing", "Connect the text to encode to the input.", EOutcome.NotExecuted));
        }
        if (input.Kind != EValueKind.Json || input.Json.ValueKind != JsonValueKind.String)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.encode.input.type", "The input must be text."));
        }
        NodeSettings settings = new(context.Settings);
        string requested = settings.ReadText("encoding") ?? "utf-8", bom = settings.ReadText("bom") ?? "auto";
        string automatic = "auto", present = "present";
        try
        {
            Encoding encoding = TextEncodings.Utf8;
            if (!string.Equals(requested, automatic, StringComparison.Ordinal))
            {
                encoding = TextEncodings.Resolve(requested);
            }
            bool includeBom = string.Equals(bom, present, StringComparison.Ordinal)
                || string.Equals(bom, automatic, StringComparison.Ordinal) && encoding.CodePage is 1200 or 1201 or 12000 or 12001;
            if (includeBom && encoding.Preamble.IsEmpty)
            {
                return ValueTask.FromResult(NodeResult.Failure("text.encode.bom.unsupported", "The selected encoding does not define a Unicode BOM."));
            }
            byte[] bytes = encoding.GetBytes(input.Json.GetString()!);
            if (includeBom)
            {
                bytes = [.. encoding.Preamble, .. bytes];
            }
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromBytes(bytes)));
        }
        catch (EncoderFallbackException)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.encode.characters.invalid", "The text contains characters that the selected encoding cannot represent."));
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.encode.characters.invalid", "The text contains invalid Unicode characters."));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return ValueTask.FromResult(NodeResult.Failure("text.encode.encoding.unavailable", "The selected encoding is unavailable on this runtime."));
        }
    }
}
