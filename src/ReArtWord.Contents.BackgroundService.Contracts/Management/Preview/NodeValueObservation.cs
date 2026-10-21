using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>値本体を公開せず、処理中と最後に確認した成否を観測します。</summary>
public sealed record NodeValueObservation(Guid ExecutionId, Guid CausationId, string WorkflowId, string Revision, string NodeId,
    string PeerId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null, EOutcome? Outcome = null);
