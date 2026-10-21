using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

/// <summary>期限で勝手に解放しないファイル予約の所有情報です。</summary>
public sealed record FileLease(Guid Id, string OwnerPeerId, Guid OwnerInstanceId, PeerInfo Coordinator, IReadOnlyList<PeerInfo> Members, IReadOnlyList<FileFootprint> Footprints, bool LocalOnly = false, Guid ReservationId = default, DateTimeOffset AdmissionExpiresAt = default);
