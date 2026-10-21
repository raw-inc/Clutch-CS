using System.Buffers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>ファイルシステム用Pathを、OSに依存しない区切りと可搬な名前規則で扱います。</summary>
public static class PathValues
{
    private static readonly string[] devices = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];
    private static readonly SearchValues<char> forbidden = SearchValues.Create("<>:\"|?*");

    /// <summary>JSON文字列として運び、Pathであることだけを型情報へ残します。</summary>
    /// <returns>区切りを正規化したPath値です。</returns>
    public static NodeValue Create(string text) => NodeValue.FromObject(Normalize(text)) with { DataType = new("path") };

    /// <summary>URL、ワイルドカード、デバイスパスを通常のファイル名と取り違えません。</summary>
    /// <returns>基本はスラッシュ表記、UNCは別OSでローカル絶対パスと誤認しないWindows表記です。</returns>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || 32768 < text.Length)
        {
            throw new FormatException("path.length");
        }
        TextValues.ValidateUnicode(text);
        string path = text.Replace('\\', '/');
        bool drive = 1 < path.Length && char.IsAsciiLetter(path[0]) && path[1] == ':';
        bool unc = path.StartsWith("//", StringComparison.Ordinal);
        if (drive && (path.Length < 3 || path[2] != '/'))
        {
            throw new FormatException("path.driveRelative");
        }
        string prefix = drive ? path[..3]
                              : unc ? "//"
                                    : path.StartsWith('/') ? "/"
                                                           : string.Empty;
        string[] parts = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool invalidShare = unc && (parts.Length < 2 || parts[0] is "." or ".." or "?" || parts[1] is "." or "..");
        if (invalidShare)
        {
            throw new FormatException("path.unc");
        }
        for (int index = 0; index < parts.Length; index++)
        {
            ValidateName(parts[index], !unc || 2 <= index);
        }
        string result = prefix + string.Join('/', parts);
        if (path.EndsWith('/') && !result.EndsWith('/'))
        {
            result += "/";
        }
        return unc ? result.Replace('/', '\\')
                   : result;
    }

    /// <summary>実行先の区切りへ変換し、別OSのドライブや共有をローカル相対パスへ読み替えません。</summary>
    /// <returns>指定OSで解釈できるファイルパスです。</returns>
    public static string ToNative(string text, bool windows)
    {
        string path = Normalize(text);
        bool drive = 1 < path.Length && path[1] == ':';
        bool unc = path.StartsWith("\\\\", StringComparison.Ordinal);
        if (!windows && (drive || unc) || windows && path.StartsWith('/') && !unc)
        {
            throw new FormatException("path.foreignRoot");
        }
        return windows ? path.Replace('/', '\\')
                       : path;
    }

    /// <summary>後続の絶対パスで前半を黙って捨てず、相対要素を順番に連結します。</summary>
    /// <returns>区切りを一つ補ったPathです。</returns>
    public static NodeValue Join(IReadOnlyList<NodeValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new FormatException("path.inputs");
        }
        string result = Normalize(values[0].GetString() ?? throw new FormatException("path.string"));
        foreach (NodeValue value in values.Skip(1))
        {
            string part = Normalize(value.GetString() ?? throw new FormatException("path.string"));
            if (part.StartsWith('/') || part.StartsWith('\\') || 1 < part.Length && part[1] == ':')
            {
                throw new FormatException("path.rootedSegment");
            }
            if (32768 < result.Length + part.Length + 1)
            {
                throw new FormatException("path.length");
            }
            result = result.TrimEnd('/', '\\') + "/" + part;
        }
        return Create(result);
    }

    /// <summary>ファイルAPIによる切捨てや予約デバイスへの解釈を事前に拒否します。</summary>
    private static void ValidateName(string name, bool checkDevice)
    {
        bool navigation = name is "." or "..";
        if (navigation)
        {
            return;
        }
        if (name.AsSpan().IndexOfAny(forbidden) != -1 || name.Any(character => character < 32) || name.EndsWith('.') || name.EndsWith(' '))
        {
            throw new FormatException("path.invalidName");
        }
        string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        bool numberedDevice = stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
        if (checkDevice && (devices.Contains(stem, StringComparer.Ordinal) || numberedDevice))
        {
            throw new FormatException("path.reservedName");
        }
    }
}
