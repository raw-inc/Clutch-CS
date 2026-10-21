using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Collaboration;

/// <summary>一つのブラウザ実体の編集位置を通知します。</summary>
public sealed record PresenceUpdate(string WorkflowId, Guid UiInstanceId, string HostName, string DisplayName, IReadOnlyList<string> SelectedNodeIds, double? CursorX = null, double? CursorY = null, bool Dirty = false, IReadOnlyList<string>? DirtyNodeIds = null,
    string? DragNodeId = null, NodePosition? DragPosition = null);
