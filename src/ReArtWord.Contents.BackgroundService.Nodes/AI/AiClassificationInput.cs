using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>四媒体を公式Chat Completionsの対応する入力部品へ変換します。</summary>
internal static class AiClassificationInput
{
    /// <summary>動画は明示した時点の画像に限り、それ以外は元媒体を渡します。</summary>
    /// <returns>API入力内容と検証用のサンプル時点です。</returns>
    internal static async Task<IReadOnlyList<object>> CreateAsync(NodeExecutionContext context, MediaOptions media, string instruction, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        string kind = settings.ReadText("mediaKind")!;
        NodeValue value = context.Inputs["value"];
        List<object> content = [new { type = "text", text = instruction }];
        string textKind = "text";
        string videoKind = "video";
        string audioKind = "audio";
        if (string.Equals(kind, textKind, StringComparison.Ordinal))
        {
            string? text = value.GetString();
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            content.Add(new { type = "text", text });
            return content;
        }
        string format = settings.ReadText("inputFormat")!;
        if (string.Equals(kind, videoKind, StringComparison.Ordinal))
        {
            string path = InputPath(context, media, value);
            _ = await MediaArtifact.ProbeAsync(media, path, kind, cancellationToken).ConfigureAwait(false);
            foreach (JsonElement time in settings.Get("frameTimesSeconds").EnumerateArray())
            {
                string temporary = MediaArtifact.TemporaryPath(media, path + ".frame." + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    new FileAccessPolicy(media.Files).ValidatePath(path);
                    string[] arguments = ["-nostdin", "-v", "error", "-n", "-ss", time.GetDouble().ToString(CultureInfo.InvariantCulture), "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska,webm,avi", "-i", path, "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=512:-2", "-f", "image2", "-update", "1", "-c:v", "png", temporary];
                    _ = await BoundedProcessRunner.RunAsync(media.FfmpegPath, arguments, cancellationToken).ConfigureAwait(false);
                    byte[] bytes = await ReadBoundedAsync(temporary, cancellationToken).ConfigureAwait(false);
                    content.Add(new { type = "text", text = "sampleSeconds=" + time.GetDouble().ToString(CultureInfo.InvariantCulture) });
                    content.Add(Image(bytes, "png"));
                }
                finally
                {
                    MediaArtifact.Cleanup(media, temporary);
                }
            }
            return content;
        }
        byte[] input;
        if (value.Kind == EValueKind.Binary)
        {
            if (33554432 < value.Binary.Length)
            {
                throw new InvalidDataException("分類入力が32MiBを超えました。");
            }
            input = value.Binary.ToArray();
        }
        else
        {
            string path = InputPath(context, media, value);
            _ = await MediaArtifact.ProbeAsync(media, path, kind, cancellationToken).ConfigureAwait(false);
            input = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        }
        if (string.Equals(kind, audioKind, StringComparison.Ordinal))
        {
            content.Add(new { type = "input_audio", input_audio = new { data = Convert.ToBase64String(input), format } });
        }
        else
        {
            content.Add(Image(input, format));
        }
        return content;
    }

    /// <summary>画像を公式base64 データ URL入力へ変換します。</summary>
    /// <returns>Chat Completionsのimage部品です。</returns>
    private static object Image(byte[] bytes, string format)
    {
        return new { type = "image_url", image_url = new { url = "data:image/" + format + ";base64," + Convert.ToBase64String(bytes) } };
    }

    /// <summary>同じPeerの生成物または明示パスだけを解決します。</summary>
    /// <returns>許可ルート内の入力パスです。</returns>
    private static string InputPath(NodeExecutionContext context, MediaOptions media, NodeValue value)
    {
        string? path = value.GetString();
        string peerKey = "peerId";
        if (path is null && value.Json.ValueKind == JsonValueKind.Object && value.Json.TryGetProperty(peerKey, out JsonElement peer) && string.Equals(peer.GetString(), context.PeerId, StringComparison.Ordinal))
        {
            path = value.Json.GetProperty("path").GetString();
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileAccessPolicy(media.Files).ResolvePath(path);
    }

    /// <summary>分類用の媒体を明示上限内で読みます。</summary>
    /// <returns>最大32MiBの媒体内容です。</returns>
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken = default)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using MemoryStream output = new();
        await AiRequestSession.CopyBoundedAsync(input, output, 33554432, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }
}
