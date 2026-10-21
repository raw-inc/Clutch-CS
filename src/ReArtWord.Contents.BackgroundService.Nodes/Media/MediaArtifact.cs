using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Media;

/// <summary>生成済みメディアを検証してファイルの実体情報を返します。</summary>
// 抑制理由: 媒体種別と設定キーは保存設定および外部形式の固定契約です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "媒体種別と設定キーの定数は互換性を維持します。")]
internal static class MediaArtifact
{
    private const string KeyAudio = "audio";

    /// <summary>許可パスの実ファイルを検査し、formatとストリームを取得します。</summary>
    /// <returns>ffprobeが検証したメディア情報です。</returns>
    internal static async Task<JsonElement> ProbeAsync(MediaOptions options, string path, string kind, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy policy = new(options.Files);
        policy.ValidatePath(path);
        string[] arguments = ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "wav,mp3,flac,ogg,mov,matroska,webm,avi,png_pipe,jpeg_pipe,webp_pipe", "-show_entries", "format=format_name,duration:stream=codec_type,codec_name,width,height,sample_rate,channels", "-of", "json", path];
        string result = await BoundedProcessRunner.RunAsync(options.FfprobePath, arguments, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(result);
        JsonElement root = document.RootElement;
        string expected = "video";
        if (string.Equals(kind, KeyAudio, StringComparison.Ordinal))
        {
            expected = KeyAudio;
        }
        bool validFormat = root.TryGetProperty("format", out JsonElement format) && format.TryGetProperty("format_name", out JsonElement name) && !string.IsNullOrWhiteSpace(name.GetString());
        bool validStreams = root.TryGetProperty("streams", out JsonElement streams) && streams.EnumerateArray().Any(stream => stream.TryGetProperty("codec_type", out JsonElement type) && string.Equals(type.GetString(), expected, StringComparison.Ordinal));
        if (!validFormat || !validStreams)
        {
            throw new InvalidDataException("生成ファイルに要求されたメディアstreamがありません。");
        }
        return root.Clone();
    }

    /// <summary>保存先を変更せず、一時媒体の検証情報を作ります。</summary>
    /// <returns>確定処理が成功してから公開するファイル情報です。</returns>
    internal static async Task<NodeValue> InspectAsync(MediaOptions options, string temporaryPath, string outputPath, string peerId, string mediaType, string kind, CancellationToken cancellationToken = default)
    {
        JsonElement probe = await ProbeAsync(options, temporaryPath, kind, cancellationToken).ConfigureAwait(false);
        RequireFormat(probe, mediaType);
        FileAccessPolicy policy = new(options.Files);
        policy.ValidatePath(temporaryPath);
        byte[] hash;
        long size;
        using (FileStream input = new(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        {
            size = input.Length;
            if (size == 0)
            {
                throw new InvalidDataException("生成ファイルが空です。");
            }
            hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return NodeValue.FromObject(new { peerId, path = outputPath, mediaType, size, sha256 = Convert.ToHexStringLower(hash), probe });
    }

    /// <summary>許可出力の隣に一意の一時パスを作ります。</summary>
    /// <returns>出力と同一ファイルシステム上の新規パスです。</returns>
    internal static string TemporaryPath(MediaOptions options, string outputPath, bool allowExisting = false)
    {
        FileAccessPolicy policy = new(options.Files);
        string output = policy.ResolvePath(outputPath);
        if (!allowExisting && File.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output)))
        {
            throw new IOException("出力先が既存、または親ディレクトリがありません。");
        }
        return policy.ResolvePath(output + "." + Guid.NewGuid().ToString("N") + ".partial");
    }

    /// <summary>未確定の自分の一時ファイルだけを取り除きます。</summary>
    internal static void Cleanup(MediaOptions options, string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            new FileAccessPolicy(options.Files).ValidatePath(path);
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 外部のパス変更やファイル占有時は安全な範囲の削除を優先し残存を許します。
        }
    }

    /// <summary>検出したcontainerと要求MIME種別の一致を検証します。</summary>
    private static void RequireFormat(JsonElement probe, string mediaType)
    {
        string format = probe.GetProperty("format").GetProperty("format_name").GetString()!;
        string expected = mediaType switch
        {
            "audio/wav" => "wav",
            "audio/mpeg" => "mp3",
            "audio/flac" => "flac",
            "video/mp4" => "mp4",
            "video/webm" => "webm",
            "image/png" => "png_pipe",
            "image/jpeg" => "jpeg_pipe",
            "image/webp" => "webp_pipe",
            _ => throw new InvalidDataException("未対応の出力媒体形式です。"),
        };
        if (!format.Split(',').Contains(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException("生成ファイルのcontainerが要求形式と一致しません。");
        }
    }
}
