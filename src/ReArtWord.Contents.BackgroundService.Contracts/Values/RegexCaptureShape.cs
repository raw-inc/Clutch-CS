using System.Globalization;
using System.Text.RegularExpressions;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>一般的な構文の単一キャプチャだけを証明し、不明な構文は配列へ保守的に分類します。</summary>
internal static class RegexCaptureShape
{
    /// <summary>実Regexが決めた名前を使用し、繰り返す親グループも子の出力へ反映します。</summary>
    /// <returns>一一致内に複数キャプチャを持ち得るグループ名です。</returns>
    internal static HashSet<string> Repeated(Regex expression, string pattern)
    {
        HashSet<string> all = expression.GetGroupNames().Where(name => !string.Equals(name, "0", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> repeated = new(StringComparer.Ordinal);
        Stack<HashSet<string>> stack = new();
        int unnamed = 0;
        for (int index = 0; index < pattern.Length; index++)
        {
            char symbol = pattern[index];
            if (symbol == '\\')
            {
                index++;
                continue;
            }
            if (symbol == '[')
            {
                if (!SkipClass(pattern, ref index))
                {
                    return all;
                }
                continue;
            }
            if (symbol == '(')
            {
                string? name = null;
                if (index + 1 < pattern.Length && pattern[index + 1] == '?')
                {
                    if (index + 2 == pattern.Length)
                    {
                        return all;
                    }
                    char kind = pattern[index + 2];
                    if (kind is '<' or '\'' && !(kind == '<' && index + 3 < pattern.Length && pattern[index + 3] is '=' or '!'))
                    {
                        char end = kind == '<' ? '>'
                                               : '\'';
                        int close = pattern.IndexOf(end, index + 3);
                        if (close == -1)
                        {
                            return all;
                        }
                        name = pattern[(index + 3)..close];
                        if (name.Contains('-', StringComparison.Ordinal) || int.TryParse(name, CultureInfo.InvariantCulture, out _))
                        {
                            return all;
                        }
                        index = close;
                    }
                    else
                    {
                        if (kind is not (':' or '=' or '!' or '>' or '<'))
                        {
                            // inline options、コメント、条件分岐は.NET側で実行し、独自に単一とは推測しません。
                            return all;
                        }
                        index += kind == '<' ? 3
                                             : 2;
                    }
                }
                else
                {
                    name = (++unnamed).ToString(CultureInfo.InvariantCulture);
                }
                HashSet<string> captures = new(StringComparer.Ordinal);
                if (name is not null)
                {
                    captures.Add(name);
                    if (!seen.Add(name))
                    {
                        repeated.Add(name);
                    }
                }
                stack.Push(captures);
                continue;
            }
            if (symbol == ')')
            {
                if (!stack.TryPop(out HashSet<string>? captures))
                {
                    return all;
                }
                if (CanRepeat(pattern, index + 1))
                {
                    repeated.UnionWith(captures);
                }
                if (stack.TryPeek(out HashSet<string>? parent))
                {
                    parent.UnionWith(captures);
                }
            }
        }
        return stack.Count == 0 && all.SetEquals(seen) ? repeated
                                                       : all;
    }

    /// <summary>文字クラス内部の括弧をグループと扱わず、複雑な差集合は未確定とします。</summary>
    /// <returns>閉じ位置を確認できた場合だけtrueです。</returns>
    private static bool SkipClass(string pattern, ref int index)
    {
        int start = index + 1;
        if (start < pattern.Length && pattern[start] == '^')
        {
            start++;
        }
        for (index++; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                index++;
                continue;
            }
            if (pattern[index] == '[')
            {
                return false;
            }
            if (pattern[index] == ']' && start < index)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>グループ全体の最大反復回数が1を超える可能性を確認します。</summary>
    /// <returns>不定回数または複数回の反復ならtrueです。</returns>
    private static bool CanRepeat(string pattern, int index)
    {
        if (pattern.Length <= index)
        {
            return false;
        }
        if (pattern[index] is '*' or '+')
        {
            return true;
        }
        if (pattern[index] != '{')
        {
            return false;
        }
        int close = pattern.IndexOf('}', index + 1);
        if (close == -1)
        {
            return false;
        }
        string range = pattern[(index + 1)..close];
        string maximum = range.Split(',')[^1];
        return !int.TryParse(maximum, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || 1 < count;
    }
}
