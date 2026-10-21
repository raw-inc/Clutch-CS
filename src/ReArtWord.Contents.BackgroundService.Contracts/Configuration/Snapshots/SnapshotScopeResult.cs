namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>復元操作へそのまま固定して渡す、明示範囲の展開結果です。</summary>
/// <param name="Targets">実Peerとノード集合。</param>
/// <param name="Issues">不明対象や成立不能条件。</param>
public sealed record SnapshotScopeResult(IReadOnlyList<SnapshotTarget> Targets, IReadOnlyList<ValidationIssue> Issues);
