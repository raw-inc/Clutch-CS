namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>取得・共有できたスナップショットを返します。</summary>
/// <param name="Result">取得保存結果。</param>
/// <param name="Snapshot">取得できたスナップショット。</param>
public sealed record SnapshotCaptureResult(ConfigurationPhaseResult Result, SnapshotRecord? Snapshot);
