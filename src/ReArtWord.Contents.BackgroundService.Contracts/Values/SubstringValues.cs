using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>C#のUTF-16位置・長さを使い、実行と接続プレビューに同じ部分文字列を返します。</summary>
public static class SubstringValues
{
    /// <summary>範囲を丸めず、長さ省略だけを末尾までの指定として扱います。</summary>
    /// <returns>指定範囲の文字列です。</returns>
    public static NodeValue Extract(NodeValue? value, NodeValue? startIndex, NodeValue? length)
    {
        if (value?.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("substring.text.required");
        }
        string text = value.GetString()!;
        int start = Integer(startIndex);
        int count = text.Length - start;
        if (length is not null && !(length.Kind == EValueKind.Json && length.Json.ValueKind == JsonValueKind.Null))
        {
            count = Integer(length);
        }
        if (start < 0 || text.Length < start || count < 0 || text.Length - start < count)
        {
            throw new FormatException("substring.range");
        }
        string result = text.Substring(start, count);
        TextValues.ValidateUnicode(result);
        return NodeValue.FromObject(result);
    }

    /// <summary>文字列や小数を位置へ暗黙変換しません。</summary>
    /// <returns>Int32の位置または長さです。</returns>
    private static int Integer(NodeValue? value)
    {
        if (value?.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.Number || !value.Json.TryGetInt32(out int result))
        {
            throw new FormatException("substring.integer.required");
        }
        return result;
    }
}
