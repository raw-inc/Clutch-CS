namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>転送済みアーカイブを変更前に検査します。</summary>
/// <param name="OperationId">アップロード時に固定した操作IDです。</param>
/// <param name="Mode">既存定義の扱いです。</param>
public sealed record ArchivePreviewRequest(Guid OperationId, EArchiveImportMode Mode);
