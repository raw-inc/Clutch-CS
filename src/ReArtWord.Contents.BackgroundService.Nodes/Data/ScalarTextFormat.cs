using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>数値から文字列への明示書式を、実行OSのカルチャーに依存せず適用します。</summary>
internal static class ScalarTextFormat
{
    /// <summary>文字列系はそのまま返し、書式未指定では従来のJSON表記を維持します。</summary>
    /// <returns>書式付き数値、または元のスカラー表記です。</returns>
    internal static string Convert(NodeValue value, NodeValue? format)
    {
        if (value.Json.ValueKind == JsonValueKind.String)
        {
            return value.Json.GetString()!;
        }
        if (value.Json.ValueKind == JsonValueKind.Number)
        {
            _ = ScalarNumbers.Read(value);
        }
        if (format is null || format.Kind == EValueKind.Json && format.Json.ValueKind == JsonValueKind.Null)
        {
            return value.Json.GetRawText();
        }
        if (format.Kind != EValueKind.Json || format.Json.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("format.type");
        }
        string pattern = format.Json.GetString()!;
        if (pattern.Length == 0)
        {
            return value.Json.GetRawText();
        }
        if (value.Json.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException("format.unsupported");
        }
        Validate(pattern);
        IFormattable number = Number(value, pattern);
        string? result;
        try
        {
            result = number.ToString(pattern, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            result = null;
        }
        return result ?? throw new FormatException("format.invalid");
    }

    /// <summary>標準書式の巨大な精度指定を、文字列を割り当てる前に拒否します。</summary>
    private static void Validate(string pattern)
    {
        if (256 < pattern.Length || pattern.Any(char.IsControl))
        {
            throw new FormatException("format.range");
        }
        bool standard = char.IsAsciiLetter(pattern[0]) && !pattern.AsSpan(1).ContainsAnyExceptInRange('0', '9');
        if (standard && 1 < pattern.Length && (!int.TryParse(pattern.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int precision) || 1024 < precision))
        {
            throw new FormatException("format.range");
        }
    }

    /// <summary>整数と十進数の精度を優先し、その範囲外だけ有限binary64を使用します。</summary>
    /// <returns>入力に対応した.NET数値です。</returns>
    private static IFormattable Number(NodeValue value, string pattern)
    {
        bool integral = pattern[0] is 'D' or 'd' or 'X' or 'x' or 'B' or 'b';
        if (integral && !pattern.AsSpan(1).ContainsAnyExceptInRange('0', '9'))
        {
            return ScalarNumbers.Integer(ScalarNumbers.Token(value));
        }
        if (value.Json.TryGetInt64(out long integer))
        {
            return integer;
        }
        if (value.Json.TryGetDecimal(out decimal number) && (number != 0 || ScalarNumbers.Read(value) == 0))
        {
            return number;
        }
        return ScalarNumbers.Read(value);
    }
}
