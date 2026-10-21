using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>明示エンドポイントと任意API項目の送信規則を共有します。</summary>
internal static class AiRequestSettings
{
    /// <summary>省略時だけ公式URLを使い、指定したURLにはパスを追加しません。</summary>
    /// <returns>認証を送る一つの完全URLです。</returns>
    internal static Uri Endpoint(NodeSettings settings, Uri fallback)
    {
        string? endpoint = settings.ReadText("endpoint");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return fallback;
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new FormatException("Specify a complete HTTP or HTTPS API endpoint without user information or a fragment.");
        }
        return uri;
    }

    /// <summary>任意項目は入力されているものだけコピーし、API側の既定値を維持します。</summary>
    internal static void Copy(JsonElement settings, JsonObject body, params (string Setting, string Api)[] names)
    {
        foreach ((string setting, string api) in names)
        {
            if (settings.TryGetProperty(setting, out JsonElement value) && value.ValueKind != JsonValueKind.Null)
            {
                body[api] = JsonNode.Parse(value.GetRawText());
            }
        }
    }
}
