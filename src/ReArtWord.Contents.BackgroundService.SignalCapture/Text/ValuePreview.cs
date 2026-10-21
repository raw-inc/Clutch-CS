using System.Text;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Text;

/// <summary>一覧用の値だけを有界化し、パケット原本と詳細表示から分離します。</summary>
internal sealed class ValuePreview
{
    private static int maximumCharacters => 256;
    private static int maximumHexBytes => 64;
    private static string hexDigits => "0123456789ABCDEF";
    private readonly StringBuilder text = new();
    private bool truncated;

    /// <summary>表示可能な範囲を超える入力を受け取ったか。</summary>
    internal bool IsTruncated => truncated;

    /// <summary>制御文字と改行を可視化しながら、上限内に追記します。</summary>
    internal void Append(string value)
    {
        Span<char> characters = stackalloc char[2];
        foreach (System.Text.Rune rune in value.EnumerateRunes())
        {
            if (truncated)
            {
                return;
            }

            string? escaped = null;
            if (System.Text.Rune.IsControl(rune))
            {
                escaped = rune.Value switch
                {
                    10 => "\\n",
                    13 => "\\r",
                    9 => "\\t",
                    _ => $"\\x{rune.Value:X2}"};
            }

            int length = escaped?.Length ?? rune.Utf16SequenceLength;
            if (maximumCharacters < text.Length + length)
            {
                text.Append("...");
                truncated = true;
                return;
            }

            if (escaped is not null)
            {
                text.Append(escaped);
            }
            else
            {
                int written = rune.EncodeToUtf16(characters);
                text.Append(characters[..written]);
            }
        }
    }

    /// <summary>可視化済みの一覧値を返します。</summary>
    /// <returns>長い値には省略記号を付けます。</returns>
    public override string ToString() => text.ToString();

    /// <summary>取得済みデータの先頭を Hex で表示します。</summary>
    /// <returns>空白区切りの Hex。</returns>
    internal static string Hex(ReadOnlySpan<byte> data)
    {
        int length = Math.Min(data.Length, maximumHexBytes);
        Span<char> result = stackalloc char[maximumHexBytes * 3 + 3];
        int written = 0;
        for (int i = 0; i < length; i++)
        {
            if (i != 0)
            {
                result[written++] = ' ';
            }

            result[written++] = hexDigits[data[i] >> 4];
            result[written++] = hexDigits[data[i] & 15];
        }

        if (length < data.Length)
        {
            result[written++] = '.';
            result[written++] = '.';
            result[written++] = '.';
        }

        return new string (result[..written]);
    }
}
