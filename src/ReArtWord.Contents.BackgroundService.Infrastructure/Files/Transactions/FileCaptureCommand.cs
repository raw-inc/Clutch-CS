using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>全対象予約と元の実行を結び付けた事前退避です。</summary>
internal sealed record FileCaptureCommand(RemoteExecutionRequest Request, FileLease Lease);
