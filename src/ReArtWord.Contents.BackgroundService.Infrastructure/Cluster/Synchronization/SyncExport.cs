using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>Storeが回収する固定ファイル・接続と、作成後に延長しない期限です。</summary>
internal sealed record SyncExport(string Path, SyncManifest Manifest, SyncExportStream Transfer, string[] Hashes, SyncSnapshotIndex Index, SyncLifetime Lifetime)
{
    /// <summary>本文を含まない保持境界です。</summary>
    internal RepositorySnapshot Metadata => Index.Metadata;
}
