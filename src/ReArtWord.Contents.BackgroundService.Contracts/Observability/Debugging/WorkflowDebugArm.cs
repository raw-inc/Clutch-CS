namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;

/// <summary>次の一件を予約する実体固定のチケットです。</summary>
public sealed record WorkflowDebugArm(Guid Id, string WorkflowId, string OwnerPeerId, Guid OwnerInstanceId, DateTimeOffset ExpiresAt);
