using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

/// <summary>退避・補償・解放の確認結果を実操作から区別します。</summary>
public sealed record FilePhaseResult(EOutcome Outcome, string? Reason = null, Guid? OperationId = null, string? PeerId = null, Guid? InstanceId = null);
