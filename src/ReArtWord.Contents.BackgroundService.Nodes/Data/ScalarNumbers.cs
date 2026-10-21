using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>明示変換と四則演算で有限数値の読み方を共有します。</summary>
internal static class ScalarNumbers
{
    /// <summary>JSON numberだけを有限binary64として読みます。</summary>
    /// <returns>有限の数値です。</returns>
    internal static double Read(NodeValue value)
    {
        if (value.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException("number.type");
        }
        return Parse(Token(value));
    }

    /// <summary>Invariant形式を読み、非有限値と非ゼロのunderflowを拒否します。</summary>
    /// <returns>有限のbinary64値です。</returns>
    internal static double Parse(string token)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new FormatException("number.format");
        }
        string mantissa = token.Split('e', 'E')[0];
        if (!double.IsFinite(value) || (value == 0 && mantissa.Any(character => '1' <= character && character <= '9')))
        {
            throw new FormatException("number.range");
        }
        return value;
    }

    /// <summary>数値か文字列のスカラーだけから変換トークンを読みます。</summary>
    /// <returns>長さを制限した入力です。</returns>
    internal static string Token(NodeValue value)
    {
        string token = value.Json.ValueKind switch
        {
            JsonValueKind.String => value.Json.GetString()!.Trim(),
            JsonValueKind.Number => value.Json.GetRawText(),
            _ => throw new FormatException("number.type"),
        };
        if (4096 < token.Length)
        {
            throw new FormatException("number.range");
        }
        return token;
    }

    /// <summary>小数を丸めず、指数を含む十進入力を正確にInt64へ変換します。</summary>
    /// <returns>小数部がゼロで範囲内の整数です。</returns>
    internal static long Integer(string token)
    {
        _ = Parse(token);
        string[] parts = token.Split('e', 'E');
        string mantissa = parts[0];
        string minus = "-";
        string plus = "+";
        bool negative = mantissa.StartsWith(minus, StringComparison.Ordinal);
        if (negative || mantissa.StartsWith(plus, StringComparison.Ordinal))
        {
            mantissa = mantissa[1..];
        }
        int point = mantissa.IndexOf('.', StringComparison.Ordinal);
        int fraction = 0;
        if (0 <= point)
        {
            fraction = mantissa.Length - point - 1;
            mantissa = mantissa.Remove(point, 1);
        }
        string digits = mantissa.TrimStart('0');
        if (digits.Length == 0)
        {
            return 0;
        }
        int exponent = 0;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            throw new FormatException("integer.range");
        }
        long scale = (long)exponent - fraction;
        if (scale < 0)
        {
            long retained = digits.Length + scale;
            if (retained < 0 || digits.AsSpan((int)Math.Max(0, retained)).ContainsAnyExcept('0'))
            {
                throw new FormatException("integer.fractional");
            }
            digits = digits[..(int)retained];
        }
        else
        {
            if (19 < digits.Length + scale)
            {
                throw new FormatException("integer.range");
            }
            digits += new string('0', (int)scale);
        }
        string signed = digits;
        if (negative)
        {
            signed = minus + digits;
        }
        if (!long.TryParse(signed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            throw new FormatException("integer.range");
        }
        return integer;
    }
}
