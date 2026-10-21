using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>担当変更を越えて維持する、時間指定の基準時刻と受付済み回の位置です。</summary>
/// <param name="Key">同じ時間指定と実行区分を識別するキー。</param>
/// <param name="Version">比較更新に使う記録の版。</param>
/// <param name="Anchor">間隔指定などの基準となる最初の有効化時刻。</param>
/// <param name="LastScheduledAt">最後に受付記録を確定した予定時刻。</param>
/// <param name="Count">受付記録を確定した回数。</param>
/// <param name="Outcome">記録の確認結果。</param>
/// <param name="Reason">確認できない場合の理由。</param>
/// <param name="Exhausted">実行対象となる予定が一回もないことを記録済みか。</param>
public sealed record ScheduleCursor(string Key, string? Version, DateTimeOffset Anchor, DateTimeOffset? LastScheduledAt = null, long Count = 0,
    EOutcome Outcome = EOutcome.Succeeded, string? Reason = null, bool Exhausted = false);
