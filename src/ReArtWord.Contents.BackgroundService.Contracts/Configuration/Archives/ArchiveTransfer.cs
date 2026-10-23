namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>アーカイブの転送に使用するサイズと検証情報。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="Length">ZIP全体のバイト数です。</param>
/// <param name="Sha256">ZIP全体のSHA-256です。</param>
public sealed record ArchiveTransfer(Guid OperationId, long Length, string Sha256);
