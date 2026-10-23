namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>事前確認した内容で復元を開始する要求。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="Confirmation">事前確認で取得した確認トークン。</param>
public sealed record ArchiveImportRequest(Guid OperationId, string Confirmation);
