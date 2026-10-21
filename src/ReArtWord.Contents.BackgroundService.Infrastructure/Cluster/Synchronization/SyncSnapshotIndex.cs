using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>固定ファイル内のJSON要素の位置だけを保持し、履歴の値は保持しません。</summary>
internal sealed record SyncSnapshotIndex(RepositorySnapshot Metadata, IReadOnlyDictionary<string, IReadOnlyList<SyncSnapshotRange>> Sections);

