namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>照会時点で実行中だった処理と、次ページの位置を返します。</summary>
/// <param name="Items">実行先と開始元の実体を含む実行状態。</param>
/// <param name="Next">続きがなければnull。</param>
public sealed record ActiveRunsPage(IReadOnlyList<WorkflowRunStatus> Items, Guid? Next);
