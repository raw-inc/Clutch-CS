using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>予定の次回時刻と、直近一回の確認済み結果です。</summary>
/// <param name="Id">予定識別子です。</param>
/// <param name="NextAt">次回の予定時刻です。</param>
/// <param name="LastAt">直近の受付済み予定時刻です。</param>
/// <param name="Outcome">直近の結果です。</param>
/// <param name="Reason">見送り・失敗・未確認の理由です。</param>
public sealed record ProcessScheduleStatus(string Id, DateTimeOffset? NextAt, DateTimeOffset? LastAt = null, EOutcome? Outcome = null, string? Reason = null);
