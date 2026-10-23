namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>パスを外部へ公開しない一回のアーカイブ転送です。</summary>
/// <param name="OperationId">元の操作IDです。</param>
/// <param name="Length">ZIP全体のバイト数です。</param>
/// <param name="Sha256">ZIP全体のSHA-256です。</param>
public sealed record ArchiveTransfer(Guid OperationId, long Length, string Sha256);
