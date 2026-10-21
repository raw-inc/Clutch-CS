using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

/// <summary>同じ設定世代で固定した全対象を補償付きで実行します。</summary>
public sealed record FileTransactionRequest(Guid OperationId, IReadOnlyList<RemoteExecutionRequest> Targets);
