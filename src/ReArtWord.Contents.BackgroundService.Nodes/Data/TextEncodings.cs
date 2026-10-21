using System.Text;
using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>文字コード変換ノードの対応形式とBOM判別を共通化します。</summary>
internal static class TextEncodings
{
    // UTF-32LEとUTF-16LEの先頭が共通なので、自動判別では長いBOMから確認します。
    private static readonly Encoding[] unicode = [new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
        new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)];

    /// <summary>BOMを自動で除去しない、厳密なUTF-8復号器です。</summary>
    internal static Encoding Utf8 => unicode[2];

    /// <summary>対応文字コードとBOMの選択肢を両方向で揃えます。</summary>
    /// <returns>各方向の既定値と説明を持つ設定です。</returns>
    internal static string Schema(bool encode)
    {
        JsonObject root = JsonNode.Parse("""
            {"type":"object","properties":{
              "encoding":{"type":"string","enum":["auto","utf-8","utf-16le","utf-16be","utf-32le","utf-32be","shift_jis","euc-jp","iso-2022-jp","us-ascii","iso-8859-1","windows-1252"],"default":"auto","description":"Automatic detects a Unicode BOM; without a BOM it uses strict UTF-8. Select legacy encodings explicitly. Explicit UTF-16 takes precedence over an overlapping UTF-32 BOM prefix."},
              "bom":{"type":"string","enum":["auto","present","none"],"default":"auto","description":"Automatic removes a matching leading BOM when present. Present requires it. None treats every byte as text, including a leading U+FEFF; automatic encoding then uses UTF-8. Conflicting BOMs and invalid byte sequences fail."}
            }}
            """)!.AsObject();
        if (encode)
        {
            root["properties"]!["encoding"]!["default"] = "utf-8";
            root["properties"]!["encoding"]!["description"] = "Choose the output encoding. Automatic uses UTF-8. Characters that the selected encoding cannot represent fail without replacement.";
            root["properties"]!["bom"]!["description"] = "Automatic adds a BOM for UTF-16/UTF-32 and omits it for UTF-8 and legacy encodings. Present adds a Unicode BOM and fails for encodings without one. None adds nothing. Text content is preserved.";
        }
        return root.ToJsonString();
    }

    /// <summary>明示したUnicode形式を優先し、自動指定では長いBOMから照合します。</summary>
    /// <returns>一致したBOMを持つ文字コード、またはBOMなしです。</returns>
    internal static Encoding? DetectBom(ReadOnlySpan<byte> bytes, Encoding? selected)
    {
        if (selected is not null && !selected.Preamble.IsEmpty && bytes.StartsWith(selected.Preamble))
        {
            return selected;
        }
        foreach (Encoding encoding in unicode)
        {
            if (bytes.StartsWith(encoding.Preamble))
            {
                return encoding;
            }
        }
        return null;
    }

    /// <summary>OSの既定文字コードへ依存せず、すべての復号で例外フォールバックを使います。</summary>
    /// <returns>不正バイトを置換しない文字コードです。</returns>
    internal static Encoding Resolve(string name)
    {
        Encoding? encoding = name switch
        {
            "utf-8" => unicode[2],
            "utf-16le" => unicode[3],
            "utf-16be" => unicode[4],
            "utf-32le" => unicode[0],
            "utf-32be" => unicode[1],
            _ => null,
        };
        if (encoding is not null)
        {
            return encoding;
        }
        int codePage = name switch
        {
            "shift_jis" => 932,
            "euc-jp" => 51932,
            "iso-2022-jp" => 50220,
            "us-ascii" => 20127,
            "iso-8859-1" => 28591,
            "windows-1252" => 1252,
            _ => throw new ArgumentException("Unsupported encoding.", nameof(name)),
        };
        return CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            ?? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
