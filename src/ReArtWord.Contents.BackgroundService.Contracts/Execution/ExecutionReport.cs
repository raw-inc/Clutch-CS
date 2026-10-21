namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>完了した一件のワークフロー実行を表します。</summary>
/// <param name="Id">一件の実行識別子。</param>
/// <param name="WorkflowId">ワークフロー。</param>
/// <param name="Revision">開始時の設定版。</param>
/// <param name="Outcome">一件の実行全体の結果。</param>
/// <param name="StartedAt">受付時刻。</param>
/// <param name="CompletedAt">完了時刻。</param>
/// <param name="Nodes">実行・未実行を含むノード結果。</param>
public sealed record ExecutionReport(
    Guid Id,
    string WorkflowId,
    string Revision,
    EOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<NodeExecutionReport> Nodes);
