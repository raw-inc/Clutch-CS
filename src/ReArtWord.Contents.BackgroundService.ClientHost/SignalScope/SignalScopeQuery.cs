using System.Net;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>本文とHexを、索引用の巨大文字列を追加保持せずに検索します。</summary>
internal sealed class SignalScopeQuery
{
    private readonly SignalScopeExpression expression;
    private readonly Dictionary<SignalScopeExpression, byte[]> hex = [];
    private readonly bool hexadecimal;

    /// <summary>全式を先に検証し、不完全な入力で前回の条件を壊しません。</summary>
    internal SignalScopeQuery(SignalScopeExpression? expression, string? mode)
    {
        this.expression = expression ?? new(Term: string.Empty);
        string hexMode = "hex", textMode = "text";
        hexadecimal = string.Equals(mode, hexMode, StringComparison.Ordinal);
        if (mode is not null && !hexadecimal && !string.Equals(mode, textMode, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unknown advanced search mode.");
        }
        int nodes = 0, characters = 0;
        Validate(this.expression, 0, ref nodes, ref characters);
    }

    /// <summary>深さ・項目数・文字数とHexの完全なbyte対を検証します。</summary>
    private void Validate(SignalScopeExpression value, int depth, ref int nodes, ref int characters)
    {
        if (32 < depth || 512 < ++nodes)
        {
            throw new ArgumentException("Advanced search expression is too complex.");
        }
        if (value.Term is { } term)
        {
            characters += term.Length;
            if (2048 < characters || value.Left is not null || value.Right is not null || value.Op is not null)
            {
                throw new ArgumentException("Invalid advanced search term.");
            }
            if (hexadecimal)
            {
                string compact = string.Concat(term.Where(character => !char.IsWhiteSpace(character)));
                if (compact.Length % 2 != 0 || compact.Any(character => !Uri.IsHexDigit(character)))
                {
                    throw new ArgumentException("Hex search requires complete byte pairs.");
                }
                hex[value] = Convert.FromHexString(compact);
            }
            return;
        }
        string and = "and", or = "or";
        if ((!string.Equals(value.Op, and, StringComparison.Ordinal) && !string.Equals(value.Op, or, StringComparison.Ordinal)) || value.Left is null || value.Right is null)
        {
            throw new ArgumentException("Invalid advanced search operator.");
        }
        Validate(value.Left, depth + 1, ref nodes, ref characters);
        Validate(value.Right, depth + 1, ref nodes, ref characters);
    }

    /// <summary>最大9000bytesの原本を一回だけ一時領域へ展開します。</summary>
    /// <returns>全条件を満たすかです。</returns>
    internal bool Matches(SignalScopePacket packet, string hostName, IReadOnlyDictionary<string, string> names)
    {
        if (expression.Term?.Length == 0)
        {
            return true;
        }
        Span<char> decoded = stackalloc char[9000];
        int length = hexadecimal ? 0
                                 : Encoding.UTF8.GetChars(packet.Raw, decoded);
        return Matches(expression, packet, decoded[..length], hostName, names);
    }

    /// <summary>論理演算を短絡評価し、原本や検索式をログへ出しません。</summary>
    /// <returns>部分式の一致です。</returns>
    private bool Matches(SignalScopeExpression value, SignalScopePacket packet, ReadOnlySpan<char> decoded, string hostName, IReadOnlyDictionary<string, string> names)
    {
        if (value.Term is { } term)
        {
            if (hexadecimal)
            {
                return 0 <= packet.Raw.AsSpan().IndexOf(hex[value]);
            }
            SignalScopeRow row = packet.Row;
            return term.Length == 0 || decoded.Contains(term, StringComparison.OrdinalIgnoreCase)
                || packet.Details.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Source.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Destination.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Protocol.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Transport.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Time.Contains(term, StringComparison.OrdinalIgnoreCase) || hostName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || names.GetValueOrDefault(row.Source, string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)
                || names.GetValueOrDefault(row.Destination, string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase);
        }
        string and = "and";
        return string.Equals(value.Op, and, StringComparison.Ordinal) ? Matches(value.Left!, packet, decoded, hostName, names) && Matches(value.Right!, packet, decoded, hostName, names)
                                                                      : Matches(value.Left!, packet, decoded, hostName, names) || Matches(value.Right!, packet, decoded, hostName, names);
    }

    /// <summary>設定をすべて検証してから、新しい名前集合を作ります。</summary>
    /// <returns>外から変更されない辞書です。</returns>
    internal static IReadOnlyDictionary<string, string> Names(SignalScopeName[] values)
    {
        if (1024 < values.Length)
        {
            throw new ArgumentException("Too many capture host names.");
        }
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (SignalScopeName value in values)
        {
            if (value is null || value.Address is null || value.Name is null || 128 < value.Address.Length || 256 < value.Name.Length || !IPAddress.TryParse(value.Address, out _))
            {
                throw new ArgumentException("Invalid capture host name.");
            }
            names[value.Address] = value.Name;
        }
        return names;
    }
}
