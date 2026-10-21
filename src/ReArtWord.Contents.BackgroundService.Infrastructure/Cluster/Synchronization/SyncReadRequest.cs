namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>同じ固定書出しの連続したバイト範囲だけを要求します。</summary>
internal sealed record SyncReadRequest(Guid Id, long Offset, int Length);
