using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>復元全体と対象別の実行結果です。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="Outcome">全体結果。</param>
/// <param name="Targets">対象ごとの適用・補償。</param>
/// <param name="Reason">全体の非成功理由。</param>
public sealed record SnapshotRestoreResult(Guid OperationId, EOutcome Outcome, IReadOnlyList<SnapshotTargetResult> Targets, string? Reason = null);
