using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

/// <summary>元の実行結果と、実行前退避への補償結果を保持します。</summary>
public sealed record FileTargetTransactionResult(string PeerId, Guid InstanceId, Guid ExecutionId, NodeResult Execution, FilePhaseResult Preparation, FilePhaseResult Compensation, FilePhaseResult Release, FileCompensationToken? Backup = null);
