using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>復元内容、復元前の設定、参加ピアを記録した復元計画。</summary>
internal sealed record ArchiveImportPlan(ArchiveImportPreview Preview, ArchiveTransfer File, ClusterArchiveConfiguration Before,
    ClusterArchiveConfiguration Incoming, IReadOnlyList<PeerInfo> Members);
