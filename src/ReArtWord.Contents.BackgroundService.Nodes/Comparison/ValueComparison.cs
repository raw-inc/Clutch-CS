using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;

namespace ReArtWord.Contents.BackgroundService.Nodes.Comparison;

/// <summary>値の判定と条件分岐で、型と数値精度を保つ比較を共有します。</summary>
internal static class ValueComparison
{
    /// <summary>条件分岐と集合処理で同じスカラー比較を使用します。</summary>
    /// <returns>指定比較の結果、未知の演算ならnullです。</returns>
    internal static bool? Test(NodeValue left, NodeValue right, string operation)
    {
        string equal = "equal";
        string notEqual = "notEqual";
        if (string.Equals(operation, equal, StringComparison.Ordinal))
        {
            return Equal(left, right);
        }
        if (string.Equals(operation, notEqual, StringComparison.Ordinal))
        {
            return !Equal(left, right);
        }
        int order = Order(left, right);
        return operation switch
        {
            "less" => order < 0,
            "lessOrEqual" => order <= 0,
            "greater" => 0 < order,
            "greaterOrEqual" => 0 <= order,
            _ => null,
        };
    }

    /// <summary>文字列への変換をせず、JSON構造またはバイト列を比較します。</summary>
    /// <returns>型と内容が同じかです。</returns>
    internal static bool Equal(NodeValue left, NodeValue right) => left.Kind == right.Kind
        && ((left.Kind == EValueKind.Binary && left.Binary.Span.SequenceEqual(right.Binary.Span))
            || (left.Kind == EValueKind.Json && JsonElement.DeepEquals(left.Json, right.Json)));

    /// <summary>十進表現を既存の正規化へ渡し、doubleへの丸めや巨大な指数展開を避けます。</summary>
    /// <returns>左が小さいと負、同じなら0、大きいと正です。</returns>
    internal static int Order(NodeValue left, NodeValue right)
    {
        if (left.Kind != EValueKind.Json || right.Kind != EValueKind.Json || left.Json.ValueKind != JsonValueKind.Number || right.Json.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException("number.type");
        }
        (BigInteger first, int firstExponent) = ExactDecimal.Normalize(ScalarNumbers.Token(left));
        (BigInteger second, int secondExponent) = ExactDecimal.Normalize(ScalarNumbers.Token(right));
        int sign = first.Sign.CompareTo(second.Sign);
        if (sign != 0 || first.IsZero)
        {
            return sign;
        }
        string firstDigits = BigInteger.Abs(first).ToString(CultureInfo.InvariantCulture);
        string secondDigits = BigInteger.Abs(second).ToString(CultureInfo.InvariantCulture);
        int magnitude = ((long)firstDigits.Length + firstExponent).CompareTo((long)secondDigits.Length + secondExponent);
        if (magnitude != 0)
        {
            return magnitude * first.Sign;
        }
        int length = Math.Max(firstDigits.Length, secondDigits.Length);
        return string.CompareOrdinal(firstDigits.PadRight(length, '0'), secondDigits.PadRight(length, '0')) * first.Sign;
    }
}
