using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>固定要求と各段階を別Peerの回復にも残します。</summary>
internal sealed record FileTransactionOperation(string Fingerprint, FileTransactionRequest Request, IReadOnlyList<PeerInfo> Members, string OwnerPeerId, Guid OwnerInstanceId, FileLease? Lease, FileTransactionResult Result, bool ReservationConfirmed = false);
