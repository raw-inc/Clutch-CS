namespace ReArtWord.Contents.BackgroundService.Contracts.Retention;

/// <summary>作成日からの非圧縮期間と、圧縮後を含む総保持期間です。</summary>
/// <param name="RawDays">非圧縮で保持する日数です。</param>
/// <param name="TotalDays">作成日から削除までの日数です。nullは圧縮して永久保存します。</param>
public sealed record RetentionWindow(int RawDays = 7, int? TotalDays = 30)
{
    /// <summary>削除が圧縮より先になる設定や、扱えない日数を拒否します。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(RawDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RawDays, 36500);
        if (TotalDays is int total)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(total, RawDays);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(total, 36500);
        }
    }
}
