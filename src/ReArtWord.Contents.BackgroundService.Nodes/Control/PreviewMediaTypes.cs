namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>ブラウザーの媒体要素へ渡す形式を限定し、任意HTMLや外部URLを表示しません。</summary>
internal static class PreviewMediaTypes
{
    /// <summary>明示形式と生成物の形式を同じ許可集合で確認します。</summary>
    /// <returns>媒体要素へ渡せる形式ならtrueです。</returns>
    internal static bool Supports(string? type) => type is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp"
        or "audio/wav" or "audio/mpeg" or "audio/ogg" or "audio/flac" or "audio/mp4" or "audio/webm" or "video/mp4" or "video/webm" or "video/ogg";

    /// <summary>バイナリー入力のコンテナーを識別します。codecの再生可否はブラウザーで確認します。</summary>
    /// <returns>既知形式、または未対応を示すnullです。</returns>
    internal static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return "image/png";
        }
        if (data.StartsWith(new byte[] { 255, 216, 255 }))
        {
            return "image/jpeg";
        }
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }
        if (12 <= data.Length && data.StartsWith("RIFF"u8))
        {
            return data[8..12] switch
            {
                var format when format.SequenceEqual("WEBP"u8) => "image/webp",
                var format when format.SequenceEqual("WAVE"u8) => "audio/wav",
                _ => null,
            };
        }
        if (data.StartsWith("BM"u8))
        {
            return "image/bmp";
        }
        if (data.StartsWith("OggS"u8))
        {
            return "audio/ogg";
        }
        if (data.StartsWith("fLaC"u8))
        {
            return "audio/flac";
        }
        if (data.StartsWith("ID3"u8) || (2 <= data.Length && data[0] == 255 && (data[1] & 224) == 224))
        {
            return "audio/mpeg";
        }
        if (12 <= data.Length && data[4..8].SequenceEqual("ftyp"u8))
        {
            return "video/mp4";
        }
        if (data.StartsWith(new byte[] { 26, 69, 223, 163 }))
        {
            return "video/webm";
        }
        return null;
    }
}
