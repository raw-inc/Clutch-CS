namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;

/// <summary>現在の占有と先頭の有限件数だけを返す、履歴と分離した表示情報です。</summary>
/// <param name="WorkflowId">対象ワークフロー。</param>
/// <param name="NodeId">制限開始ノード。</param>
/// <param name="ActiveCount">強制進行分も含む占有数。</param>
/// <param name="WaitingCount">全待機数。</param>
/// <param name="Waiting">受付順の待機表示。</param>
/// <param name="ObservedAt">担当で状態を確認した時刻。</param>
/// <param name="Problem">取得できない場合の理由。空配列を待機なしの証明へ使いません。</param>
public sealed record WorkflowConcurrencySnapshot(string WorkflowId, string NodeId, int ActiveCount, int WaitingCount,
    IReadOnlyList<WorkflowConcurrencyWaiter> Waiting, DateTimeOffset ObservedAt, string? Problem = null);
