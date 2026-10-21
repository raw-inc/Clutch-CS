using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>編集画面の診断に必要な成否だけを保持し、入力・出力・自由記述メッセージは含めません。</summary>
/// <param name="WorkflowId">実行したワークフローです。</param>
/// <param name="Revision">実行時の設定版です。</param>
/// <param name="NodeId">実行したノードです。</param>
/// <param name="PeerId">実行先です。</param>
/// <param name="Outcome">確認した成否です。</param>
/// <param name="ReasonCode">安定した理由識別子です。</param>
/// <param name="CompletedAt">元の実行が完了した時刻です。</param>
public sealed record NodeExecutionObservation(string WorkflowId, string Revision, string NodeId, string PeerId, EOutcome Outcome, string? ReasonCode, DateTimeOffset CompletedAt);
