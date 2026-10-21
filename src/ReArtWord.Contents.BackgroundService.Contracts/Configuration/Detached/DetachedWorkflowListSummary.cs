using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;

/// <summary>離脱中のローカル版と未完了統合を本文なしで一覧します。</summary>
public sealed record DetachedWorkflowListSummary(string WorkflowId, string Name, string LocalRevision, string? BaseRevision,
    string? CurrentRevision, int ConflictCount, Guid? IntegrationOperationId = null, EOutcome? IntegrationOutcome = null);
