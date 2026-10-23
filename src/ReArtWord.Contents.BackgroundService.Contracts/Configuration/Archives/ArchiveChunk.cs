namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>同じ位置への再送は内容一致を確認する有限サイズの断片です。</summary>
/// <param name="OperationId">元の操作IDです。</param>
/// <param name="Offset">先頭からの位置です。</param>
/// <param name="DataBase64">断片のBase64です。</param>
public sealed record ArchiveChunk(Guid OperationId, long Offset, string DataBase64);
