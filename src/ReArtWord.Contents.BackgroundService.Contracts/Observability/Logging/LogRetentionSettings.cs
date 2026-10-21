namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>Peer固有の保存先や通信設定を含まない、共有するログ保持方針です。</summary>
/// <param name="Rules">レベル別の保持期間。</param>
/// <param name="RollingTime">全レベル共通の日付境界。</param>
/// <param name="TimeZoneId">日付境界のタイムゾーン。</param>
/// <param name="ApplyToExisting">短縮した削除期限を既存ログへ適用するか。</param>
/// <param name="ExpandCompressed">非圧縮期間延長時に既存圧縮ログを展開するか。</param>
public sealed record LogRetentionSettings(IReadOnlyList<LogRetentionRule> Rules, string RollingTime, string TimeZoneId,
    bool ApplyToExisting = false, bool ExpandCompressed = false);
