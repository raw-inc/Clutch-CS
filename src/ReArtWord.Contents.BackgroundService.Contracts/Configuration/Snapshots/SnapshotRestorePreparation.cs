namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>副作用を開始せず、元復元要求と未保存編集を照合した結果です。</summary>
public sealed record SnapshotRestorePreparation(Guid OperationId, string Fingerprint, IReadOnlyList<SnapshotTarget> Targets, IReadOnlyList<SnapshotEditorState> Editors, IReadOnlyList<SnapshotEditLock> Locks, bool Ready, string? Reason = null);
