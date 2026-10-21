using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>同じ有限バッチの書出しを、応答喪失後も作り直さず照会します。</summary>
internal sealed record SyncHistoryRequest(Guid Id, IReadOnlyList<RecoveryRecord> Receipts, DateTimeOffset ExpiresAt);
