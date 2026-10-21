using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

/// <summary>固定した全対象の実行・補償・解放を確認した結果です。</summary>
public sealed record FileTransactionResult(Guid OperationId, EOutcome Outcome, IReadOnlyList<FileTargetTransactionResult> Targets, string? Reason = null, FileLease? Reservation = null, FilePhaseResult? ReservationRelease = null);
