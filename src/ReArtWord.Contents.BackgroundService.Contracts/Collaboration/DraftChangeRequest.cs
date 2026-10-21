using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Collaboration;

/// <summary>保存とは独立した編集元と手元の変更を共有します。</summary>
public sealed record DraftChangeRequest(Guid OperationId, string WorkflowId, Guid UiInstanceId, string? BaseRevision, string? BaseSessionVersion, WorkflowDefinition Original, WorkflowDefinition Edited, IReadOnlyDictionary<string, EEditResolution> Resolutions);
