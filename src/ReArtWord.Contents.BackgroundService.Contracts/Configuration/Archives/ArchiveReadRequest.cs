namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>転送位置を固定する読取り要求です。</summary>
/// <param name="OperationId">元の操作IDです。</param>
/// <param name="Offset">読取り位置です。</param>
public sealed record ArchiveReadRequest(Guid OperationId, long Offset);
