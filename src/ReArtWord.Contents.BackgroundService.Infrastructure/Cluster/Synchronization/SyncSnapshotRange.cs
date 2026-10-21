namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>区分に対応する一要素の、区切り文字を含まない連続範囲です。</summary>
internal readonly record struct SyncSnapshotRange(int Bucket, long Offset, long Length);
