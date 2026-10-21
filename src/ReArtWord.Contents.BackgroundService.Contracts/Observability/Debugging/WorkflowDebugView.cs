namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;

/// <summary>一時記録の予約・取得状態と、本文を展開しない診断ページです。</summary>
public sealed record WorkflowDebugView(WorkflowDebugArm Arm, EWorkflowDebugState State, Guid? ExecutionId = null, string? Revision = null, string? CapturePeerId = null, Guid? CaptureInstanceId = null, string? Reason = null, IReadOnlyList<WorkflowDebugNode>? Nodes = null, int TotalNodes = 0, int? NextOffset = null, bool Truncated = false, Execution.EOutcome? Outcome = null);
