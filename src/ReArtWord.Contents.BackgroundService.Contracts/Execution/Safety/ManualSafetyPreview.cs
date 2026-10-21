namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

/// <summary>一時操作で変わる対象と、現在確認できる安全判断です。</summary>
/// <param name="WorkflowId">対象ワークフロー。</param>
/// <param name="Scope">ノードまたは事前定義した安全Group。</param>
/// <param name="WorkflowRevision">今回確認した対象定義版。</param>
/// <param name="ConfirmationRequired">明示設定により直前確認が必要か。</param>
/// <param name="Targets">現在の対象ノード。</param>
/// <param name="Impact">確認できる影響と確認できない範囲。</param>
public sealed record ManualSafetyPreview(string WorkflowId, string Scope, string WorkflowRevision, bool ConfirmationRequired, IReadOnlyList<ManualSafetyTarget> Targets, string Impact);
