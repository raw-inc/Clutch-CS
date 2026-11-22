using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>固定要求と各段階を別Peerの回復にも残します。</summary>
internal sealed record FileTransactionOperation(string Fingerprint, FileTransactionRequest Request, IReadOnlyList<PeerInfo> Members, string OwnerPeerId, Guid OwnerInstanceId, FileLease? Lease, FileTransactionResult Result, bool ReservationConfirmed = false)
{
    /// <summary>要求の保存形式から復元し、回復時に生成方式を変更しません。</summary>
    [JsonIgnore]
    internal EDerivedIdFormat IdentityFormat
    {
        get; init;
    }
}
