namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>ログを含めるかどうかを指定した書き出し要求。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="IncludeLogs">保持中のログ本文・履歴を含める場合はtrue。</param>
public sealed record ArchiveExportRequest(Guid OperationId, bool IncludeLogs = false);
