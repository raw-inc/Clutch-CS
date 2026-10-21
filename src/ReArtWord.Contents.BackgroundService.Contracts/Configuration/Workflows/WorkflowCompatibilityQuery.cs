namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>選択したPeerの実装と、保存・有効設定および取得済み状態の対応を調べます。</summary>
/// <param name="WorkflowId">確認するワークフロー。</param>
/// <param name="SnapshotAfter">前ページで返したスナップショット位置。</param>
/// <param name="Limit">一ページで確認するスナップショット数。</param>
public sealed record WorkflowCompatibilityQuery(string WorkflowId, string? SnapshotAfter = null, int Limit = 20);
