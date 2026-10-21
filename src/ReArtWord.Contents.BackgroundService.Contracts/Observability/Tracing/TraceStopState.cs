namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>明示停止が進行中または未解決のため追跡再開始を保留している状態です。</summary>
/// <param name="OperationId">停止操作。nullなら再開始保留なし。</param>
/// <param name="Version">明示解除に必要な版。</param>
public sealed record TraceStopState(Guid? OperationId, string? Version);
