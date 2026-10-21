namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

/// <summary>未開始予約の登録・比較変更です。</summary>
/// <param name="OperationId">予約変更自身の再照会IDです。</param>
/// <param name="Plan">変更後の固定入力です。</param>
/// <param name="ExpectedVersion">新規はnull、変更時は確認した保存版です。</param>
public sealed record UpdateScheduleRequest(Guid OperationId, UpdateSchedulePlan Plan, string? ExpectedVersion = null);
