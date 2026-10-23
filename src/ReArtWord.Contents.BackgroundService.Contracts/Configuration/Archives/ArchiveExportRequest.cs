namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>ログの包含を明示する書出し要求です。</summary>
/// <param name="OperationId">元の操作IDです。</param>
/// <param name="IncludeLogs">保持中のログ本文・履歴を含むかです。</param>
public sealed record ArchiveExportRequest(Guid OperationId, bool IncludeLogs = false);
