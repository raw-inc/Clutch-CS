namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>同期処理の受付・断片・取込確認を小さい応答で区別します。</summary>
internal sealed record SyncResponse(SyncManifest? Manifest = null, bool Conflict = false, string? Reason = null, bool Pending = false, SyncChunk? Chunk = null, string? Version = null,
    SyncInventoryOffer? Inventory = null, bool HistoryRetention = false, bool StagedRecovery = false);
