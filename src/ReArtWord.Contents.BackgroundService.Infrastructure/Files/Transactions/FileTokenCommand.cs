using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>元の一件に限って退避を補償・解放します。</summary>
internal sealed record FileTokenCommand(RemoteExecutionRequest Request, FileCompensationToken Token);
