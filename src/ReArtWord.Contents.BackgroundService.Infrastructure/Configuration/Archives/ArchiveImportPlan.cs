using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>確認画面の対象、変更前状態、変更後状態と参加実体を固定します。</summary>
internal sealed record ArchiveImportPlan(ArchiveImportPreview Preview, ArchiveTransfer File, ClusterArchiveConfiguration Before,
    ClusterArchiveConfiguration Incoming, IReadOnlyList<PeerInfo> Members);
