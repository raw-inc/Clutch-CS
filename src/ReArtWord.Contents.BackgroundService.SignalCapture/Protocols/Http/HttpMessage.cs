namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocols.Http;

/// <summary>境界を検証した HTTP/1 メッセージです。</summary>
internal sealed record HttpMessage(string StartLine, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body)
{
    /// <summary>レスポンスであるか。</summary>
    internal bool IsResponse => StartLine.StartsWith("HTTP/1.", StringComparison.Ordinal);

    /// <summary>指定ヘッダーの最初の値を返します。</summary>
    /// <returns>存在しない場合は空文字。</returns>
    internal string Header(string name)
    {
        foreach (KeyValuePair<string, string> item in Headers)
        {
            if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value;
            }
        }

        return "";
    }
}
