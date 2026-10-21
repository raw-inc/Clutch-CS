namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

/// <summary>現在Peerが取り込んだ明示停止の保存版です。開始済み処理の停止確認とは別です。</summary>
public sealed record ManualSafetyStatus(string WorkflowId, string Scope, bool Stopped, string Version, bool LocalOnly, bool Continued = false,
    Guid? OperationId = null, ManualSafetyAcceptance? Acceptance = null, bool RequestedContinued = false, string? ContinuationReason = null, IReadOnlyList<Guid>? ActiveOperationIds = null);
