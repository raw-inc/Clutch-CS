namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>元のクラスタと省略情報を保持する、一行の検索結果です。</summary>
/// <param name="Source">実際に照会した取得先です。</param>
/// <param name="Entry">元のログです。</param>
/// <param name="Omission">容量上限により省略した項目です。</param>
public sealed record ScopedLogEntry(LogSearchSource Source, WorkflowLogEntry Entry, LogEntryOmission? Omission = null);
