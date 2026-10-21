using System.Buffers;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>StringとPathに共通する、文字を置換しない入力検証です。</summary>
public static class TextValues
{
    /// <summary>壊れたUTF-16をUnicode置換文字へ黙って変えません。</summary>
    public static void ValidateUnicode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ReadOnlySpan<char> remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done)
            {
                throw new FormatException("string.unicode");
            }
            remaining = remaining[consumed..];
        }
    }
}
