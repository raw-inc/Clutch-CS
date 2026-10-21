namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>追跡再開始の保留を人が解除します。受付済みのHost停止を取り消しません。</summary>
/// <param name="OperationId">解除操作の固定ID。</param>
/// <param name="ExpectedVersion">確認した停止保留の版。</param>
public sealed record TraceStopReleaseRequest(Guid OperationId, string ExpectedVersion);
