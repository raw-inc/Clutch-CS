namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;

/// <summary>現在の待機一覧を参照するワークフローと開始ノードです。</summary>
/// <param name="WorkflowId">対象ワークフロー。</param>
/// <param name="NodeId">制限開始ノード。</param>
public sealed record WorkflowConcurrencyTarget(string WorkflowId, string NodeId);
