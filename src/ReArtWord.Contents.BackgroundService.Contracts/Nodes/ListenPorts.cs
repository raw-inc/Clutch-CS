using System.Globalization;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>待受ポートの単一値・配列・範囲を同じ規則で検証します。</summary>
public static class ListenPorts
{
    /// <summary>一つのノードが同時に確保できる待受ポート数です。</summary>
    public static int MaximumCount => 256;

    /// <summary>設定入力の型と直接編集欄の意味を公開します。</summary>
    public static JsonElement Schema => JsonSerializer.Deserialize<JsonElement>("""
        {"type":["integer","string","array"],"default":0,"minimum":0,"maximum":65535,"minLength":1,"maxLength":4096,"minItems":1,"maxItems":4096,"items":{"type":"integer","minimum":0,"maximum":65535},"x-editor":"listen-ports","description":"Listen on a port, a range such as 9000-9005, or a list such as 9000, 9002, 9006-9010. Connected values accept an integer, an integer array, or text. Up to 256 distinct ports. Use 0 alone for an automatically assigned port."}
        """);

    /// <summary>重複を除き、同じ指定を常に同じ順番で確保します。省略は従来のポート0です。</summary>
    /// <returns>検証済みのポート一覧。不正な型・範囲はFormatExceptionです。</returns>
    public static IReadOnlyList<int> Read(JsonElement value)
    {
        SortedSet<int> ports = [];
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
                ports.Add(0);
                break;
            case JsonValueKind.Number:
                Add(ports, Number(value));
                break;
            case JsonValueKind.Array:
                if (4096 < value.GetArrayLength())
                {
                    throw Invalid();
                }
                foreach (JsonElement item in value.EnumerateArray())
                {
                    Add(ports, Number(item));
                }
                break;
            case JsonValueKind.String:
                ReadText(value.GetString()!, ports);
                break;
            default:
                throw Invalid();
        }
        if (ports.Count == 0 || ports.Contains(0) && ports.Count != 1)
        {
            throw Invalid();
        }
        return ports.ToArray();
    }

    /// <summary>整数以外の配列要素を暗黙変換しません。</summary>
    /// <returns>32ビット整数として取得できた値です。</returns>
    private static int Number(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int port))
        {
            throw Invalid();
        }
        return port;
    }

    /// <summary>カンマと両端を含むハイフン範囲を展開します。</summary>
    private static void ReadText(string text, SortedSet<int> ports)
    {
        if (4096 < text.Length)
        {
            throw Invalid();
        }
        foreach (string field in text.Split(',', StringSplitOptions.TrimEntries))
        {
            string[] range = field.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out int first))
            {
                throw Invalid();
            }
            int last = first;
            if (range.Length == 2 && (!int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out last) || first == 0))
            {
                throw Invalid();
            }
            if (first < 0 || last < first || 65535 < last || MaximumCount < (long)last - first + 1)
            {
                throw Invalid();
            }
            for (int port = first; port <= last; port++)
            {
                Add(ports, port);
            }
        }
    }

    /// <summary>範囲外と過大な待受数をソケット確保前に拒否します。</summary>
    private static void Add(SortedSet<int> ports, int port)
    {
        if (port is < 0 or > 65535)
        {
            throw Invalid();
        }
        ports.Add(port);
        if (MaximumCount < ports.Count)
        {
            throw Invalid();
        }
    }

    /// <summary>各入力形式で同じ設定エラーを返します。</summary>
    /// <returns>利用者が指定方法を修正できるエラーです。</returns>
    private static FormatException Invalid() => new("Specify ports from 1 to 65535 using integers, an integer array, or comma-separated ports and ascending ranges. At most 256 ports are allowed; 0 must be used alone.");
}
