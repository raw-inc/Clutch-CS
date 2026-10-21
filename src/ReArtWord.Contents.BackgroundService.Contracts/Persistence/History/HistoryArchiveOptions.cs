namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;

/// <summary>確定済みのローカル履歴本文だけを、小さいzstdブロックへ移す方針です。</summary>
/// <param name="Enabled">背景の圧縮を有効にするかです。</param>
/// <param name="KeepDays">通常形式で保持する日数です。</param>
/// <param name="MaximumHotRecords">通常形式で保持する確定履歴の件数です。</param>
/// <param name="BatchRecords">一度に移す件数の上限です。</param>
public sealed record HistoryArchiveOptions(bool Enabled = true, int KeepDays = 7, int MaximumHotRecords = 100000, int BatchRecords = 1000)
{
    /// <summary>無制限な一括処理や、不正な保持条件を起動前に拒否します。</summary>
    public void Validate()
    {
        if (KeepDays is < 0 or > 36500 || MaximumHotRecords is < 1 or > 10000000 || BatchRecords is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(HistoryArchiveOptions), "履歴圧縮の保持日数・件数・一括処理数が範囲外です。");
        }
    }
}
