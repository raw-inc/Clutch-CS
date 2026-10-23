namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>アップロードしたアーカイブの復元内容を確認する要求。</summary>
/// <param name="OperationId">アップロード時に指定した操作ID。</param>
/// <param name="Mode">復元先にだけあるWFの扱い。</param>
public sealed record ArchivePreviewRequest(Guid OperationId, EArchiveImportMode Mode);
