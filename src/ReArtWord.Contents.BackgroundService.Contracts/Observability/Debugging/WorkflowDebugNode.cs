namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;

/// <summary>一回のノード評価の経路・要約値・確認済み結果です。</summary>
public sealed record WorkflowDebugNode(long Sequence, Guid InvocationId, Guid ExecutionId, Guid? ParentExecutionId, string NodeId, string NodeType, string Stage, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, Execution.EOutcome? Outcome, string? Reason, IReadOnlyDictionary<string, string> Inputs, IReadOnlyDictionary<string, string> Outputs, IReadOnlyList<WorkflowDebugConnection> Connections);
