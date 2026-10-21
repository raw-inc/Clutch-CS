namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>作業を解放します。Completedは双方向の完全取込を確認した場合だけ指定します。</summary>
internal sealed record SyncCloseRequest(Guid ExportId, Guid ImportId, bool Completed = false);
