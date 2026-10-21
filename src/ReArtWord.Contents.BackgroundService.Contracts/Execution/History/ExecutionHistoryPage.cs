namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

/// <summary>保持中の履歴の一ページと続き位置です。</summary>
/// <param name="Entries">小さい実行結果または省略を明示した要約。</param>
/// <param name="Cursor">次ページがない場合はnull。</param>
public sealed record ExecutionHistoryPage(IReadOnlyList<ExecutionResultView> Entries, string? Cursor);
