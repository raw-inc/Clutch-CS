using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>保存元と共有草稿の比較元を分離して端末に保持します。</summary>
internal sealed record EditorDraft(WorkflowDefinition Document, WorkflowDefinition Base, string? BaseRevision,
    WorkflowDefinition Original, string? SessionVersion, string? DraftBaseRevision, bool Dirty,
    IReadOnlyList<EditConflict> Conflicts, DraftChangeRequest? Pending = null, bool NeedsBaseline = false, IReadOnlyDictionary<string, string>? InputDrafts = null, bool LocalOnly = false, IReadOnlyList<EditConflict>? SharedConflicts = null);
