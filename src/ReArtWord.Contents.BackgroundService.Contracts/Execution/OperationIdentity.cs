namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>保持期限を過ぎた要求を新規実行と取り違えない、発行時刻を持つ操作識別子です。</summary>
public static class OperationIdentity
{
    /// <summary>旧形式を推測した時刻へ置き換えず、時刻付きIDだけを読みます。</summary>
    /// <returns>妥当な発行時刻を持つ場合だけtrueです。</returns>
    public static bool TryGetIssuedAt(Guid id, out DateTimeOffset issuedAt)
    {
        issuedAt = default;
        Span<byte> bytes = stackalloc byte[16];
        _ = id.TryWriteBytes(bytes, bigEndian: true, out _);
        if ((bytes[6] >> 4) != 7 || (bytes[8] & 192) != 128)
        {
            return false;
        }
        long milliseconds = 0;
        for (int index = 0; index < 6; index++)
        {
            milliseconds = (milliseconds << 8) | bytes[index];
        }
        if (DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() < milliseconds)
        {
            return false;
        }
        issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }
}
