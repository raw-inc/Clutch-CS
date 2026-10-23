namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>アーカイブの指定位置を読み取る要求。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="Offset">ファイル先頭からのバイト位置。</param>
public sealed record ArchiveReadRequest(Guid OperationId, long Offset);
