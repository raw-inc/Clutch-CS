using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>入力を短命なプレビューへ渡す終端です。出力や後続処理を生成しません。</summary>
public sealed class PreviewNode(NodePreviewStore previews, FileSystemOptions files) : IActionNode
{
    /// <summary>一般値と媒体の自動識別・明示形式を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.preview", "Preview", "Control",
        """{"type":"object","properties":{"mediaType":{"type":"string","enum":["auto","text/plain","image/png","image/jpeg","image/gif","image/webp","image/bmp","audio/wav","audio/mpeg","audio/ogg","audio/flac","audio/mp4","audio/webm","video/mp4","video/webm","video/ogg"],"default":"auto"}}}""",
        outputs: []) with { IsTerminal = true, Description = "Display the latest input or a bounded history of text and media summaries without downstream outputs." };

    /// <summary>明示形式を設定Schemaで確認します。</summary>
    /// <returns>不正な設定です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>受信値または同じPeerの生成物を、有限のプレビューとして取得します。</summary>
    /// <returns>取得結果だけを返し、値は出力しません。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return invalid;
        }
        NodeValue? input = NodeSupport.Input(context);
        if (input is null)
        {
            return Fail(context, "preview.input");
        }
        string selected = new NodeSettings(context.Settings).ReadText("mediaType", "auto")!;
        string automatic = "auto";
        string textType = "text/plain";
        string peerKey = "peerId";
        string hashKey = "sha256";
        try
        {
            ReadOnlyMemory<byte> bytes = input.Binary;
            string? mediaType = null;
            bool artifact = input.Kind == EValueKind.Json && input.Json.ValueKind == JsonValueKind.Object
                && input.Json.TryGetProperty("peerId", out _) && input.Json.TryGetProperty("path", out _) && input.Json.TryGetProperty("mediaType", out _);
            if (artifact && !string.Equals(selected, textType, StringComparison.Ordinal))
            {
                JsonElement value = input.Json;
                if (!string.Equals(value.GetProperty(peerKey).GetString(), context.PeerId, StringComparison.Ordinal))
                {
                    return Fail(context, "preview.peer");
                }
                mediaType = value.GetProperty("mediaType").GetString();
                FileAccessPolicy policy = new(files);
                string path = policy.ResolvePath(value.GetProperty("path").GetString()!);
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                if (NodePreviewStore.MaximumMediaBytes < stream.Length)
                {
                    return Fail(context, "preview.size");
                }
                byte[] content = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
                policy.ValidatePath(path);
                if (value.TryGetProperty(hashKey, out JsonElement hash) && !string.Equals(hash.GetString(), Convert.ToHexStringLower(SHA256.HashData(content)), StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(context, "preview.changed");
                }
                bytes = content;
            }
            else
            {
                if (input.Kind == EValueKind.Json)
                {
                    string text = input.GetString() ?? input.Json.GetRawText();
                    bool truncated = 16384 < text.Length;
                    if (truncated)
                    {
                        int length = char.IsHighSurrogate(text[16383]) ? 16383
                                                                       : 16384;
                        text = text[..length];
                    }
                    previews.Publish(context, textType, Encoding.UTF8.GetBytes(text), truncated);
                    return NodeResult.Success();
                }
            }
            if (NodePreviewStore.MaximumMediaBytes < bytes.Length)
            {
                return Fail(context, "preview.size");
            }
            if (string.Equals(selected, textType, StringComparison.Ordinal))
            {
                int length = Math.Min(bytes.Length, NodePreviewStore.MaximumTextBytes);
                previews.Publish(context, textType, bytes[..length], length < bytes.Length);
                return NodeResult.Success();
            }
            mediaType = string.Equals(selected, automatic, StringComparison.Ordinal) ? mediaType ?? PreviewMediaTypes.Detect(bytes.Span)
                                                                                     : selected;
            if (!PreviewMediaTypes.Supports(mediaType))
            {
                return Fail(context, "preview.format");
            }
            previews.Publish(context, mediaType!, bytes);
            return NodeResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            return Fail(context, "preview.read");
        }
    }

    /// <summary>失敗時に前回の正常値を今回の結果として表示しません。</summary>
    /// <returns>出力を持たない取得失敗です。</returns>
    private NodeResult Fail(NodeExecutionContext context, string reason)
    {
        previews.Publish(context, "text/plain", ReadOnlyMemory<byte>.Empty, problem: reason);
        return NodeResult.Failure(reason, "Preview could not be captured.", EOutcome.Failed);
    }
}
