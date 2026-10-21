using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Collaboration;

/// <summary>未保存の候補と、人の判断が必要な項目です。</summary>
public sealed record DraftView(string WorkflowId, string? BaseRevision, string SessionVersion, WorkflowDefinition Definition, IReadOnlyList<EditConflict> Conflicts, IReadOnlyList<ValidationIssue> Validation, DateTimeOffset UpdatedAt);
