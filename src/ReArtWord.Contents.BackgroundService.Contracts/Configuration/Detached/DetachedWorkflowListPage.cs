namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;

/// <summary>ローカル変更一覧の件数を制限したページと次位置です。</summary>
public sealed record DetachedWorkflowListPage(IReadOnlyList<DetachedWorkflowListSummary> Entries, string? Cursor);
