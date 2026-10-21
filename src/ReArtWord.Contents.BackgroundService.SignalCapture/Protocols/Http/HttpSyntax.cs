namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Http;

/// <summary>HTTP/1 の開始行とフィールド構文を検証します。</summary>
internal static class HttpSyntax
{
    private static string version10 => "HTTP/1.0";
    private static string version11 => "HTTP/1.1";

    /// <summary>対応するバージョンと空白・制御文字を検証します。</summary>
    internal static void ValidateStart(string line, bool response)
    {
        string[] parts = line.Split(' ');
        string version = response ? parts[0]
                                  : parts[^1];
        if ((!string.Equals(version, version10, StringComparison.Ordinal) && !string.Equals(version, version11, StringComparison.Ordinal)) || parts.Length < 3)
        {
            throw new FormatException("An HTTP/1.0 or HTTP/1.1 start line is required.");
        }

        if (response)
        {
            if (parts[1].Length != 3 || parts[1].Any(value => value is < '0' or > '9'))
            {
                throw new FormatException("HTTP status code");
            }
        }
        else
        {
            if (parts.Length != 3 || parts[1].Length == 0 || !IsToken(parts[0]))
            {
                throw new FormatException("HTTP request line");
            }
        }

        if (line.Any(value => char.IsControl(value)))
        {
            throw new FormatException("Control character in HTTP start line");
        }
    }

    /// <summary>ヘッダー名と値の許可文字を検証します。</summary>
    /// <returns>分割したヘッダー。</returns>
    internal static KeyValuePair<string, string> Header(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !IsToken(line[..colon]) || line[(colon + 1)..].Any(value => (value < 32 && value != '\t') || value == 127))
        {
            throw new FormatException("Invalid HTTP header.");
        }

        return new KeyValuePair<string, string>(line[..colon], line[(colon + 1)..].Trim(' ', '\t'));
    }

    /// <summary>HTTP token の構成文字を検査します。</summary>
    /// <returns>空でなく、全て許可文字か。</returns>
    private static bool IsToken(string value)
    {
        return value.Length != 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal));
    }
}
