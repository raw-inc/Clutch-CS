using System.Text.Json;
using System.Text.RegularExpressions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Debugging;

/// <summary>実行値を有限の診断文字列へ変換し、バイナリ本文と認証値を展開しません。</summary>
internal static class WorkflowDebugValues
{
    private static readonly string[] sensitive = ["password", "secret", "authorization", "credential", "token", "apiKey", "api_key", "connectionString", "cookie"];

    /// <summary>多数の引数も上限を設け、省略した件数を残します。</summary>
    /// <returns>値を所有しない表示用の要約です。</returns>
    internal static IReadOnlyDictionary<string, string> Summarize(IReadOnlyDictionary<string, NodeValue> values)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((string key, NodeValue value) in values.Take(16))
        {
            result[key] = Sensitive(key) ? "[redacted]"
                                         : Value(value);
        }
        if (16 < values.Count)
        {
            result["…"] = (values.Count - 16).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more values";
        }
        return result;
    }

    /// <summary>実バイトの列挙・Base64変換を行わず種類とサイズを残します。</summary>
    /// <returns>短い値表示です。</returns>
    internal static string Value(NodeValue value)
    {
        if (value.Kind == EValueKind.Binary)
        {
            return "Binary (" + value.Binary.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes)";
        }
        try
        {
            return Text(Json(value.Json, 0), 1024);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or RegexMatchTimeoutException)
        {
            return "Summary unavailable (" + error.GetType().Name + ")";
        }
    }

    /// <summary>OSCの小さな構造は残し、大きな配列や深いObjectは省略します。</summary>
    /// <returns>構造とプリミティブ値を区別できる表現です。</returns>
    private static string Json(JsonElement value, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (3 <= depth)
                {
                    return "Object (…)";
                }
                JsonProperty[] properties = value.EnumerateObject().Take(9).ToArray();
                string suffix = properties.Length == 9 ? ", …"
                                                       : string.Empty;
                return "{" + string.Join(", ", properties.Take(8).Select(property => Text(property.Name, 80) + ": " + (Sensitive(property.Name) ? "[redacted]"
                                                                                                                                                : Json(property.Value, depth + 1)))) + suffix + "}";
            case JsonValueKind.Array:
                int count = value.GetArrayLength();
                if (3 <= depth)
                {
                    return "Array (" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                }
                string extra = 8 < count ? ", … (" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " items)"
                                         : string.Empty;
                return "[" + string.Join(", ", value.EnumerateArray().Take(8).Select(item => Json(item, depth + 1))) + extra + "]";
            case JsonValueKind.String:
                string text = Regex.Replace(value.GetString()!, "(?i)((?:password|secret|token|api_key|apikey|authorization)=)[^&\\s]+", "$1[redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
                return JsonSerializer.Serialize(Text(text, 256));
            case JsonValueKind.Undefined:
                return "Undefined";
            default:
                return value.GetRawText();
        }
    }

    /// <summary>要約中の項目名から認証情報を識別します。</summary>
    /// <returns>値を表示しない項目かです。</returns>
    private static bool Sensitive(string name) => sensitive.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    /// <summary>長い文字列を表示上限に収めます。</summary>
    /// <returns>省略を明示した文字列です。</returns>
    internal static string Text(string value, int limit) => value.Length <= limit ? value
                                                                                  : value[..limit] + "…";
}
