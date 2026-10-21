namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>適用・補償・未実施を対象別に示します。</summary>
/// <param name="Target">対象。</param>
/// <param name="Apply">選択値の適用結果。</param>
/// <param name="Compensation">開始前状態への補償結果。</param>
/// <param name="Warning">自然欠損等の警告。</param>
public sealed record SnapshotTargetResult(SnapshotTarget Target, ConfigurationPhaseResult Apply, ConfigurationPhaseResult Compensation, string? Warning = null);
