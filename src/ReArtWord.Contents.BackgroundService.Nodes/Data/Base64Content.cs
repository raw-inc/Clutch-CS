namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>有限サイズのBase64本文と、任意のdata URLのMIMEを復号します。</summary>
internal static class Base64Content
{
    /// <summary>元バイトを復元し、文字コードによる変換を挟みません。</summary>
    /// <returns>復号バイトと、data URLで指定されたMIMEです。</returns>
    internal static (byte[] Bytes, string ContentType) Decode(string text, int maximum)
    {
        if (4L * ((maximum + 2L) / 3) + 4096 < text.Length)
        {
            throw new FormatException("base64.limit");
        }
        string contentType = string.Empty;
        string data = "data:", marker = ";base64";
        if (text.StartsWith(data, StringComparison.OrdinalIgnoreCase))
        {
            int comma = text.IndexOf(',', StringComparison.Ordinal);
            if (comma is < 5 or > 1024 || !text.AsSpan(5, comma - 5).EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("base64.dataUrl");
            }
            contentType = text[5..(comma - marker.Length)];
            if (contentType.Length == 0)
            {
                contentType = "text/plain;charset=US-ASCII";
            }
            if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out _))
            {
                throw new FormatException("base64.mediaType");
            }
            text = text[(comma + 1)..];
        }
        byte[] bytes = Convert.FromBase64String(text);
        if (maximum < bytes.Length)
        {
            throw new FormatException("base64.limit");
        }
        return (bytes, contentType);
    }
}
