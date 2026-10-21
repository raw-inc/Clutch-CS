using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>確認済み所有予約を新しい解放操作の集合で除去します。</summary>
internal sealed record FileGateRelease(FileLease Lease, IReadOnlyList<PeerInfo> Members);
