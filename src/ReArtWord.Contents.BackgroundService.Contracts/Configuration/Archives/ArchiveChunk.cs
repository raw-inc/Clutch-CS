namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>分割転送するアーカイブのデータ。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="Offset">ファイル先頭からのバイト位置。</param>
/// <param name="DataBase64">転送データをBase64で表した文字列。</param>
public sealed record ArchiveChunk(Guid OperationId, long Offset, string DataBase64);
