using System.Globalization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Retention;

/// <summary>全Peerで共有するログ・履歴の保持と、状態復旧用の基準点作成条件です。</summary>
/// <param name="RawDays">種類別指定がない場合の非圧縮保持日数です。</param>
/// <param name="TotalDays">種類別指定がない場合の総保持日数です。nullは永久保存です。</param>
/// <param name="Overrides">種類ごとに共通値を上書きする保持期間です。</param>
/// <param name="CheckpointEvents">基準点作成後に確定した変更件数の閾値です。</param>
/// <param name="CheckpointDays">変更がある場合に基準点を作る経過日数です。</param>
/// <param name="RollingTime">保持日数の日付境界です。</param>
/// <param name="TimeZoneId">日付境界のタイムゾーンです。nullはクラスタ共通値を使用します。</param>
/// <param name="ApplyToExisting">短縮した削除期限を既存履歴にも適用するかです。</param>
public sealed record DataRetentionSettings(int RawDays = 7, int? TotalDays = 30,
    IReadOnlyDictionary<ERetentionCategory, RetentionWindow>? Overrides = null,
    int CheckpointEvents = 100, int CheckpointDays = 3, string RollingTime = "00:00", string? TimeZoneId = null,
    bool ApplyToExisting = false)
{
    /// <summary>明示上書き、または共通の保持期間を返します。</summary>
    /// <returns>一種類に適用する保持条件です。</returns>
    public RetentionWindow Window(ERetentionCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }
        return Overrides?.GetValueOrDefault(category) ?? new(RawDays, TotalDays);
    }

    /// <summary>全種類と両方の基準点条件を保存前に検証します。</summary>
    public void Validate()
    {
        new RetentionWindow(RawDays, TotalDays).Validate();
        if (CheckpointEvents is < 1 or > 1000000 || CheckpointDays is < 1 or > 36500)
        {
            throw new ArgumentOutOfRangeException(nameof(DataRetentionSettings), "基準点は1件以上、1日以上の有限条件で作成してください。");
        }
        string format = "HH:mm";
        if (!TimeOnly.TryParseExact(RollingTime, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException("日付境界はHH:mmで指定してください。", nameof(RollingTime));
        }
        if (TimeZoneId is not null)
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        foreach (KeyValuePair<ERetentionCategory, RetentionWindow> rule in Overrides ?? new Dictionary<ERetentionCategory, RetentionWindow>())
        {
            if (!Enum.IsDefined(rule.Key) || rule.Value is null)
            {
                throw new ArgumentException("保持対象または保持期間が不正です。", nameof(Overrides));
            }
            rule.Value.Validate();
        }
    }
}
