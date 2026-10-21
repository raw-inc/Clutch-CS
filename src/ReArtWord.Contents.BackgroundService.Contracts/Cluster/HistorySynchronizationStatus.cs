namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster;

/// <summary>業務参加の状態と独立して、背景取得の残数と異常を公開します。</summary>
/// <param name="Pending">本文取得または既存本文の照合が残る件数です。</param>
/// <param name="Problem">確認できない履歴の理由です。</param>
/// <param name="Total">今回の背景取得で登録した総件数です。</param>
public sealed record HistorySynchronizationStatus(long Pending, string? Problem = null, long Total = 0)
{
    /// <summary>内容照合と耐久保存が完了した件数です。</summary>
    public long Completed => Math.Max(0, Total - Pending);

    /// <summary>対象がある場合だけ公開する、件数に対する完了割合です。</summary>
    public double? ProgressPercent => Total == 0 ? null
                                                 : Math.Clamp(100.0 * Completed / Total, 0, 100);
}
