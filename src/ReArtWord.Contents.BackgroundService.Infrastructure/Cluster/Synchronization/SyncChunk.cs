namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>既存UDPで運べる有限の共有状態断片です。</summary>
internal sealed record SyncChunk(Guid Id, long Offset, byte[] Data);
