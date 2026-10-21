namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>元要求、Peer、UI実体と下書き版を固定した明示確認です。</summary>
public sealed record SnapshotDraftResolution(SnapshotRestoreRequest Request, string PeerId, string WorkflowId, Guid UiInstanceId, string? ExpectedSessionVersion, ESnapshotDraftAction Action);
