namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>一対象の明示選択または時点選択を指定します。</summary>
/// <param name="Target">当初の対象。</param>
/// <param name="SnapshotId">指定スナップショット。nullの場合は時点検索。</param>
/// <param name="Excluded">意図的に範囲外へ除くか。</param>
public sealed record SnapshotSelection(SnapshotTarget Target, string? SnapshotId = null, bool Excluded = false);
