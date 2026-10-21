using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

/// <summary>予約確認と、確認できない所有情報を区別します。</summary>
public sealed record FileLeaseResult(EOutcome Outcome, FileLease? Lease = null, string? Reason = null);
