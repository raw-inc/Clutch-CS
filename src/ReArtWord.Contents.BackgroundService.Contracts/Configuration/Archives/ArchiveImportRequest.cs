namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>確認した計画だけを一度実行する要求です。</summary>
/// <param name="OperationId">元の操作IDです。</param>
/// <param name="Confirmation">プレビューで確認した値です。</param>
public sealed record ArchiveImportRequest(Guid OperationId, string Confirmation);
