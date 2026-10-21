namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>軽量ワークフロー一覧と次ページ位置です。</summary>
/// <param name="Entries">本文を含まない件数を制限した一覧。</param>
/// <param name="Cursor">次ページがない場合はnull。</param>
public sealed record WorkflowListPage(IReadOnlyList<WorkflowListSummary> Entries, string? Cursor);
