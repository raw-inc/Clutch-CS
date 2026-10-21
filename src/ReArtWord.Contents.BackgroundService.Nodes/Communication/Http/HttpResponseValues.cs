using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Data;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>HTTP応答の本文バイトとメタデータを、文字列へ不可逆変換せず後続へ渡します。</summary>
internal static class HttpResponseValues
{
    /// <summary>旧envelopeを保ち、明示した新形式だけに復元可能な本文情報を追加します。</summary>
    /// <returns>本文と応答情報を分離した成功結果です。</returns>
    internal static NodeResult Create(HttpResponseMessage response, byte[] bytes, string legacyBody, string policy, string? secret)
    {
        string contentType = Redact(response.Content.Headers.ContentType?.ToString() ?? string.Empty, secret);
        string fileName = FileName(Redact(response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? string.Empty, secret));
        Dictionary<string, string[]> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers))
        {
            headers[Redact(key, secret)] = values.Select(value => Redact(value, secret)).ToArray();
        }
        string legacy = "legacy", binary = "binary";
        string format = HttpBodyFormats.ResponseFormat(policy, contentType);
        string text = Redact(legacyBody, secret);
        if (!string.Equals(policy, legacy, StringComparison.Ordinal) && !string.Equals(format, binary, StringComparison.Ordinal))
        {
            text = Redact(Decode(bytes, contentType), secret);
        }
        if (string.Equals(format, binary, StringComparison.Ordinal) && !string.IsNullOrEmpty(secret) && 0 <= bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)))
        {
            return NodeResult.Failure("http.response.secretEcho", "The binary response contains the configured authentication secret. It was not exposed or saved.");
        }
        NodeValue body = Body(format, bytes, text);
        NodeValue envelope = NodeValue.FromObject(new { statusCode = (int)response.StatusCode, body = text });
        if (!string.Equals(policy, legacy, StringComparison.Ordinal))
        {
            string encoding = "text";
            if (string.Equals(format, binary, StringComparison.Ordinal))
            {
                encoding = "base64";
                text = Convert.ToBase64String(bytes);
            }
            envelope = NodeValue.FromObject(new { statusCode = (int)response.StatusCode, body = text, bodyEncoding = encoding, contentType, fileName, headers });
        }
        return NodeResult.Success() with
        {
            Outputs = new Dictionary<string, NodeValue>
            {
                ["value"] = envelope, ["body"] = body, ["statusCode"] = NodeValue.FromObject((int)response.StatusCode),
                ["contentType"] = NodeValue.FromObject(contentType), ["fileName"] = NodeValue.FromObject(fileName), ["headers"] = NodeValue.FromObject(headers),
            }
        };
    }

    /// <summary>保存・受け渡ししたenvelopeを解析し、元バイトがない旧本文をBinaryへ偽装しません。</summary>
    /// <returns>本文と応答情報です。</returns>
    internal static NodeResult Parse(NodeValue input, string policy)
    {
        string statusKey = "statusCode", bodyKey = "body", encodingKey = "bodyEncoding";
        if (input.Kind != EValueKind.Json || input.Json.ValueKind != JsonValueKind.Object
            || !input.Json.TryGetProperty(statusKey, out JsonElement status) || !status.TryGetInt32(out int code) || code is < 100 or > 599
            || !input.Json.TryGetProperty(bodyKey, out JsonElement body) || body.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("http.response.envelope");
        }
        string contentType = Text(input.Json, "contentType");
        string format = HttpBodyFormats.ResponseFormat(policy, contentType);
        string encoded = body.GetString()!;
        string encoding = Text(input.Json, encodingKey);
        string base64 = "base64", textEncoding = "text", binary = "binary";
        byte[] bytes = [];
        string text = encoded;
        if (string.Equals(encoding, base64, StringComparison.Ordinal))
        {
            bytes = Base64Content.Decode(encoded, 16777216).Bytes;
            if (!string.Equals(format, binary, StringComparison.Ordinal))
            {
                text = Decode(bytes, contentType);
            }
        }
        else
        {
            if (encoding.Length != 0 && !string.Equals(encoding, textEncoding, StringComparison.Ordinal))
            {
                throw new FormatException("http.response.encoding");
            }
            if (string.Equals(format, binary, StringComparison.Ordinal))
            {
                throw new FormatException("http.response.bytes.unavailable");
            }
        }
        string headersKey = "headers";
        NodeValue headers = NodeValue.FromObject(new Dictionary<string, string[]>());
        if (input.Json.TryGetProperty(headersKey, out JsonElement headerValues) && headerValues.ValueKind == JsonValueKind.Object)
        {
            headers = NodeValue.FromJson(headerValues);
        }
        return NodeResult.Success() with
        {
            Outputs = new Dictionary<string, NodeValue>
            {
                ["value"] = Body(format, bytes, text), ["statusCode"] = NodeValue.FromObject(code),
                ["contentType"] = NodeValue.FromObject(contentType), ["fileName"] = NodeValue.FromObject(FileName(Text(input.Json, "fileName"))), ["headers"] = headers,
            }
        };
    }

    /// <summary>本文の指定形式だけを変換し、Binaryを文字列経由にしません。</summary>
    /// <returns>型を分けた本文です。</returns>
    private static NodeValue Body(string format, byte[] bytes, string text) => format switch
    {
        "binary" => NodeValue.FromBytes(bytes),
        "json" => NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(text)),
        _ => NodeValue.FromObject(text),
    };

    /// <summary>指定されたcharsetとBOMを確認し、不正バイトを置換しません。</summary>
    /// <returns>本文テキストです。</returns>
    private static string Decode(byte[] bytes, string contentType)
    {
        Encoding? declared = null;
        if (MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? header) && header.CharSet is { Length: > 0 } charset)
        {
            string name = charset.Trim('"');
            declared = CodePagesEncodingProvider.Instance.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        Encoding? bom = TextEncodings.DetectBom(bytes, declared);
        if (declared is not null && bom is not null && declared.CodePage != bom.CodePage)
        {
            throw new FormatException("http.response.charset.conflict");
        }
        Encoding encoding = declared ?? bom ?? TextEncodings.Utf8;
        return encoding.GetString(bytes.AsSpan(bom?.Preamble.Length ?? 0));
    }

    /// <summary>応答にechoされた接続秘密値を本文・ヘッダーの双方から除きます。</summary>
    /// <returns>後続へ公開する文字列です。</returns>
    private static string Redact(string value, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return value;
        }
        return value.Replace(secret, "[secret]", StringComparison.Ordinal);
    }

    /// <summary>応答側のディレクトリ指定はファイル名として採用しません。</summary>
    /// <returns>末尾の名前だけです。保存先は後続で明示します。</returns>
    private static string FileName(string value)
    {
        string name = value.Trim('"').Replace('\\', '/').Split('/')[^1];
        return name switch { "." or ".." => string.Empty, _ => name };
    }

    /// <summary>省略できる応答情報の文字列だけを取得します。</summary>
    /// <returns>指定文字列、または空文字です。</returns>
    private static string Text(JsonElement source, string name)
    {
        if (source.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }
        return string.Empty;
    }
}
