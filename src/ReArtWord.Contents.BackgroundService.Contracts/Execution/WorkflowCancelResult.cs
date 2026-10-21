namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>取消操作の受付結果と、元の実行で確認できた結果を分けて返します。</summary>
/// <param name="OperationId">取消操作。</param>
/// <param name="RunId">元のワークフロー実行。</param>
/// <param name="Outcome">取消要求を処理できたか。</param>
/// <param name="Requested">元の実行へ取消を要求したか。</param>
/// <param name="RunRunning">元の実行を現在も追跡中か。確認できなければnull。</param>
/// <param name="RunOutcome">元の実行の成否。処理中はUnknown。</param>
/// <param name="Reason">受付または確認不能の理由。</param>
public sealed record WorkflowCancelResult(Guid OperationId, Guid RunId, EOutcome Outcome, bool Requested,
    bool? RunRunning = null, EOutcome? RunOutcome = null, string? Reason = null);
