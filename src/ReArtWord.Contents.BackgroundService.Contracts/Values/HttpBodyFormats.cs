using System.Net.Http.Headers;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>受信MIMEの自動判定と明示指定の優先順位を一つに揃えます。</summary>
public static class HttpBodyFormats
{
    /// <summary>HTTP応答の自動形式を共通MIME判定へ接続します。</summary>
    /// <returns>未知のMIMEはバイト列として保持する形式です。</returns>
    public static string ResponseFormat(string policy, string? contentType)
    {
        string? media = null;
        if (MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? header))
        {
            media = header.MediaType;
        }
        return policy switch
        {
            "auto" => Guess(media) ?? "binary",
            "legacy" or "text" => "text",
            "json" or "binary" => policy,
            _ => throw new FormatException("http.response.format"),
        };
    }

    /// <summary>明示形式を優先し、判定不能の場合だけ指定されたfallbackを使います。</summary>
    /// <returns>本文の処理形式と後続へ通知するMIMEです。</returns>
    public static (string Format, string MediaType) Resolve(JsonElement settings, string? contentType)
    {
        string policy = Text(settings, "bodyFormat", "legacy");
        string fallback = Text(settings, "bodyFallback", "binary");
        string binaryType = Text(settings, "bodyMediaType", "application/octet-stream");
        string? media = null;
        if (MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? received))
        {
            media = received.MediaType;
        }
        string json = "json", text = "text", binary = "binary", automatic = "auto";
        if (string.Equals(policy, automatic, StringComparison.Ordinal))
        {
            string? inferred = Guess(media);
            if (inferred is not null)
            {
                return (inferred, media!);
            }
            policy = fallback;
        }
        if (string.Equals(policy, binary, StringComparison.Ordinal))
        {
            if (!MediaTypeHeaderValue.TryParse(binaryType, out MediaTypeHeaderValue? declared) || declared.MediaType is null || declared.MediaType.Contains('*', StringComparison.Ordinal) || declared.Parameters.Count != 0)
            {
                throw new FormatException("http.body.mediaType");
            }
            return (binary, declared.MediaType);
        }
        if (string.Equals(policy, json, StringComparison.Ordinal))
        {
            return (json, "application/json");
        }
        if (string.Equals(policy, text, StringComparison.Ordinal))
        {
            return (text, "text/plain");
        }
        string legacy = "legacy";
        if (!string.Equals(policy, legacy, StringComparison.Ordinal))
        {
            throw new FormatException("http.body.format");
        }
        return (legacy, media ?? "application/octet-stream");
    }

    /// <summary>曖昧なapplication独自型をJSONやテキストに決めつけません。</summary>
    /// <returns>認識できた形式、または未確定です。</returns>
    private static string? Guess(string? media) => media?.ToUpperInvariant() switch
    {
        "APPLICATION/JSON" => "json",
        not null when media.EndsWith("+json", StringComparison.OrdinalIgnoreCase) => "json",
        not null when media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) => "text",
        "APPLICATION/OCTET-STREAM" or "APPLICATION/PDF" or "APPLICATION/ZIP" or "APPLICATION/GZIP" => "binary",
        not null when media.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || media.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || media.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || media.StartsWith("font/", StringComparison.OrdinalIgnoreCase) => "binary",
        _ => null,
    };

    /// <summary>省略された旧設定を従来方式として保持します。</summary>
    /// <returns>指定値または既定値です。</returns>
    private static string Text(JsonElement settings, string key, string fallback)
        => settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()!
                                                                                                                                                        : fallback;
}
