using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>全対象予約を元の一件のローカル退避へ引き継ぎます。</summary>
internal sealed record PreparedFileOperation(RemoteExecutionRequest Request, FileLease Lease, FileCompensationToken? Token = null, bool Released = false);
