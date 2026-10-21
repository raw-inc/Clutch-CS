using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>復元範囲と競合する未保存編集の確認結果です。</summary>
public sealed record SnapshotEditorState(string PeerId, string WorkflowId, IReadOnlyList<string>? NodeIds, string? SessionVersion, bool Dirty, IReadOnlyList<PresenceView> Presence);
