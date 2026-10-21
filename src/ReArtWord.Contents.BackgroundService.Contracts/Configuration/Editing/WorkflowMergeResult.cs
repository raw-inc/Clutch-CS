using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;

/// <summary>非競合編集の統合候補と未解消項目を返します。</summary>
/// <param name="Candidate">検証対象の統合候補。</param>
/// <param name="Conflicts">未解消競合。</param>
/// <param name="Validation">意味検証結果。</param>
/// <param name="Save">保存した場合の段階結果。</param>
public sealed record WorkflowMergeResult(WorkflowDefinition? Candidate, IReadOnlyList<EditConflict> Conflicts, IReadOnlyList<ValidationIssue> Validation, WorkflowSaveResult? Save);
