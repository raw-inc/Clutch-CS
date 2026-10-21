using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

/// <summary>予約操作の結果と更新本体の結果を混同しない応答です。</summary>
/// <param name="Outcome">予約登録・変更・取消の保存結果です。</param>
/// <param name="View">確認できた予約状態です。</param>
/// <param name="Reason">失敗・不明の理由です。</param>
public sealed record UpdateScheduleResult(EOutcome Outcome, UpdateScheduleView? View, string? Reason = null);
