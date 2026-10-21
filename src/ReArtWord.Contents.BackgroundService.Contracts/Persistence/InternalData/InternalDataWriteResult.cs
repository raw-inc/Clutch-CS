using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

/// <summary>元の操作IDと保存・採用の各確認結果です。</summary>
public sealed record InternalDataWriteResult(Guid OperationId, AvailabilityWriteResult Result);
