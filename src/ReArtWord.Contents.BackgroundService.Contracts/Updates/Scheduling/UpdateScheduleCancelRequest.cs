namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;

/// <summary>開始意図の確定前だけ予約を取消します。</summary>
/// <param name="OperationId">取消操作自身のIDです。</param>
/// <param name="UpdateOperationId">予約した更新の元IDです。</param>
/// <param name="ExpectedVersion">確認した予約版です。</param>
public sealed record UpdateScheduleCancelRequest(Guid OperationId, Guid UpdateOperationId, string ExpectedVersion);
