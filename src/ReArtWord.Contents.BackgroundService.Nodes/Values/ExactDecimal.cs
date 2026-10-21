using System.Globalization;
using System.Numerics;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>十進数の暗黙丸めとunderflowを検出します。</summary>
internal static class ExactDecimal
{
    /// <summary>doubleを経由せず、元トークンと同じ値のdecimalだけを返します。</summary>
    /// <returns>精度を保持したdecimalです。</returns>
    internal static decimal Parse(string text)
    {
        string token = text.Trim();
        if (4096 < token.Length || !decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            || Normalize(token) != Normalize(value.ToString(CultureInfo.InvariantCulture)))
        {
            throw new FormatException("decimal.precisionOrRange");
        }
        return value;
    }

    /// <summary>有効数字と十進指数を正規化し、表記の違いだけを除きます。</summary>
    /// <returns>丸めない数値の比較形式です。</returns>
    internal static (BigInteger Coefficient, int Exponent) Normalize(string token)
    {
        string[] parts = token.Split('e', 'E');
        if (2 < parts.Length)
        {
            throw new FormatException("decimal.format");
        }
        string mantissa = parts[0];
        int point = mantissa.IndexOf('.', StringComparison.Ordinal);
        int scale = 0;
        if (0 <= point)
        {
            scale = mantissa.Length - point - 1;
            mantissa = mantissa.Remove(point, 1);
        }
        if (!BigInteger.TryParse(mantissa, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger coefficient))
        {
            throw new FormatException("decimal.format");
        }
        if (coefficient.IsZero)
        {
            return (BigInteger.Zero, 0);
        }
        long exponent = 0;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsedExponent))
            {
                throw new FormatException("decimal.range");
            }
            exponent = parsedExponent;
        }
        exponent -= scale;
        while (coefficient % 10 == BigInteger.Zero)
        {
            coefficient /= 10;
            exponent++;
        }
        if (exponent < int.MinValue || int.MaxValue < exponent)
        {
            throw new FormatException("decimal.range");
        }
        return (coefficient, (int)exponent);
    }
}
