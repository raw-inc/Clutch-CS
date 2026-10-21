using System.IO;
using System.Text;
using ReArtWord.Contents.BackgroundService.SignalCapture.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Http;

/// <summary>HTTP/1 の長さとチャンク境界を検証します。暗号化データを平文と誤認しません。</summary>
internal static class HttpDecoder
{
    private static string httpPrefix => "HTTP/1.";
    private static string transferHeader => "Transfer-Encoding";
    private static string chunkedCoding => "chunked";
    private static string noContentStatus => "204";
    private static string notModifiedStatus => "304";

    /// <summary>先頭の完全なメッセージだけを消費します。</summary>
    /// <returns>完全な場合はメッセージ、不完全なら null。</returns>
    internal static HttpMessage? Read(ReadOnlySpan<byte> data, bool closed, out int consumed)
    {
        consumed = 0;
        int boundary = data.IndexOf("\r\n\r\n"u8);
        if (65536 < boundary)
        {
            throw new FormatException("HTTP headers are limited to 64 KiB.");
        }

        if (boundary < 0)
        {
            if (65536 < data.Length)
            {
                throw new FormatException("HTTP headers are limited to 64 KiB.");
            }

            return null;
        }

        string[] lines = Encoding.Latin1.GetString(data[..boundary]).Split("\r\n", StringSplitOptions.None);
        string[] start = lines[0].Split(' ');
        bool response = lines[0].StartsWith(httpPrefix, StringComparison.Ordinal);
        HttpSyntax.ValidateStart(lines[0], response);
        if (start.Length < 3 || (!response && !start[^1].StartsWith(httpPrefix, StringComparison.Ordinal)))
        {
            throw new FormatException("Not an HTTP/1 start line.");
        }

        var headers = new List<KeyValuePair<string, string>>();
        foreach (string line in lines.Skip(1))
        {
            headers.Add(HttpSyntax.Header(line));
        }

        var message = new HttpMessage(lines[0], headers, Array.Empty<byte>());
        int offset = boundary + 4;
        string transfer = message.Header("Transfer-Encoding");
        if (1 < headers.Count(item => string.Equals(item.Key, transferHeader, StringComparison.OrdinalIgnoreCase)))
        {
            throw new FormatException("Duplicate HTTP Transfer-Encoding.");
        }

        string[] lengths = headers.Where(item => string.Equals(item.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)).Select(item => item.Value).ToArray();
        if (1 < lengths.Length || (0 < transfer.Length && 0 < lengths.Length))
        {
            throw new FormatException("Duplicate or conflicting HTTP length declarations.");
        }

        if (response && (start[1].StartsWith('1') || string.Equals(start[1], noContentStatus, StringComparison.Ordinal) || string.Equals(start[1], notModifiedStatus, StringComparison.Ordinal)))
        {
            consumed = offset;
            return message;
        }

        if (0 < transfer.Length)
        {
            if (!string.Equals(transfer, chunkedCoding, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("Unsupported Transfer-Encoding");
            }

            byte[]? body = ReadChunks(data[offset..], out int bodyLength);
            if (body is null)
            {
                return null;
            }

            consumed = offset + bodyLength;
            return message with
            {
                Body = body
            };
        }

        int length = 0;
        if (0 < lengths.Length)
        {
            if (!int.TryParse(lengths[0], NumberStyles.None, CultureInfo.InvariantCulture, out length) || 1024 * 1024 < length)
            {
                throw new FormatException("HTTP Content-Length must be between 0 and 1 MiB.");
            }
        }
        else
        {
            if (response)
            {
                if (!closed)
                {
                    return null;
                }

                length = data.Length - offset;
                if (1024 * 1024 < length)
                {
                    throw new FormatException("HTTP bodies are limited to 1 MiB.");
                }
            }
        }

        if (data.Length - offset < length)
        {
            return null;
        }

        consumed = offset + length;
        return message with
        {
            Body = data.Slice(offset, length).ToArray()
        };
    }

    /// <summary>ヘッダー・Path・Query と本文を表示します。</summary>
    /// <returns>最大 8192 bytes の本文プレビュー。</returns>
    internal static string Describe(HttpMessage message, bool localize = false)
    {
        var text = new StringBuilder(message.StartLine + "\n");
        if (!message.IsResponse)
        {
            string target = message.StartLine.Split(' ')[1];
            int query = target.IndexOf('?', StringComparison.Ordinal);
            string path = query < 0 ? target
                                    : target[..query];
            text.AppendLine((localize ? UiText.Get("Endpoint: ")
                                      : "Endpoint: ") + message.Header("Host"));
            text.AppendLine((localize ? UiText.Get("Path: ")
                                      : "Path: ") + path);
            if (0 <= query)
            {
                text.AppendLine((localize ? UiText.Get("Query (raw): ")
                                          : "Query (raw): ") + target[(query + 1)..]);
                foreach (string item in target[(query + 1)..].Split('&'))
                {
                    text.AppendLine("  " + Uri.UnescapeDataString(item.Replace("+", " ", StringComparison.Ordinal)));
                }
            }
        }

        foreach (KeyValuePair<string, string> header in message.Headers)
        {
            text.AppendLine(header.Key + ": " + header.Value);
        }

        text.AppendLine(string.Format(CultureInfo.InvariantCulture, localize ? UiText.Get("\nBody: {0} bytes")
                                                                             : "\nBody: {0} bytes", message.Body.Length));
        text.Append(Encoding.UTF8.GetString(message.Body, 0, Math.Min(message.Body.Length, 8192)));
        return DisplayText.Safe(text.ToString());
    }

    /// <summary>チャンクを結合して trailer 終端まで検証します。</summary>
    /// <returns>完了した本文、または null。</returns>
    private static byte[]? ReadChunks(ReadOnlySpan<byte> data, out int consumed)
    {
        consumed = 0;
        int offset = 0;
        using var body = new MemoryStream();
        while (offset < data.Length)
        {
            int end = data[offset..].IndexOf("\r\n"u8);
            if (end < 0)
            {
                return null;
            }

            string line = Encoding.ASCII.GetString(data.Slice(offset, end)).Split(';')[0];
            if (!int.TryParse(line, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int length) || length < 0 || 1024 * 1024 - body.Length < length)
            {
                throw new FormatException("HTTP chunk length");
            }

            offset += end + 2;
            if (length == 0)
            {
                if (data[offset..].StartsWith("\r\n"u8))
                {
                    consumed = offset + 2;
                }
                else
                {
                    int trailer = data[offset..].IndexOf("\r\n\r\n"u8);
                    if (trailer < 0)
                    {
                        return null;
                    }

                    consumed = offset + trailer + 4;
                    string[] trailers = Encoding.Latin1.GetString(data.Slice(offset, trailer)).Split("\r\n", StringSplitOptions.None);
                    foreach (string field in trailers)
                    {
                        HttpSyntax.Header(field);
                    }
                }

                return body.ToArray();
            }

            if (data.Length - offset - 2 < length)
            {
                return null;
            }

            if (!data.Slice(offset + length, 2).SequenceEqual("\r\n"u8))
            {
                throw new FormatException("HTTP chunk termination");
            }

            body.Write(data.Slice(offset, length));
            offset += length + 2;
        }

        return null;
    }
}
