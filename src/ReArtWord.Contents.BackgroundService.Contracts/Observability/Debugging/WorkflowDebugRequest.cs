namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;

/// <summary>同じ操作IDを再送しても次の一件を再予約しない要求です。</summary>
public sealed record WorkflowDebugRequest(Guid OperationId, string WorkflowId);
