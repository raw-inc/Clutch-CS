namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

/// <summary>実出力とは区別した、確定成否と省略範囲の要約です。</summary>
/// <param name="Id">実行識別子。</param>
/// <param name="WorkflowId">ワークフロー識別子。</param>
/// <param name="Revision">開始時の版。</param>
/// <param name="Outcome">詳細省略で変更しない実際の成否。</param>
/// <param name="StartedAt">開始時刻。</param>
/// <param name="CompletedAt">完了時刻。</param>
/// <param name="Targets">含めたノード・Peer別の成否と理由。</param>
/// <param name="TotalNodes">元のノード数。</param>
/// <param name="TotalTargets">元のPeer別結果数。</param>
public sealed record ExecutionReportSummary(Guid Id, string WorkflowId, string Revision, EOutcome Outcome,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, IReadOnlyList<ExecutionTargetSummary> Targets, int TotalNodes, int TotalTargets);
