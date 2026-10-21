using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

/// <summary>再接続時に草稿と編集の前後関係を交換します。</summary>
internal sealed record DraftAnnouncement(WorkflowDefinition Original, DraftView Draft,
    IReadOnlyDictionary<Guid, long>? Clock = null,
    IReadOnlyDictionary<Guid, string>? Operations = null);
