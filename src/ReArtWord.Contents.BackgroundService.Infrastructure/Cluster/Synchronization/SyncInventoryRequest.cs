using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>同じ照合の再配送で新しい書出しを作らないための固定要求です。</summary>
/// <param name="Id">依頼側が所有する固定スナップショットの識別子です。</param>
/// <param name="Hashes">依頼側の全区分指紋です。</param>
/// <param name="ExpiresAt">再送で延ばさない元の同期期限です。</param>
/// <param name="Boundary">依頼側が保持する非圧縮区間です。</param>
/// <param name="AllowPending">元の照合を処理中として受け取り、同じIDで結果だけを再照会できます。</param>
internal sealed record SyncInventoryRequest(Guid Id, IReadOnlyList<string> Hashes, DateTimeOffset ExpiresAt, HistoryRetentionBoundary? Boundary = null, bool AllowPending = false);
