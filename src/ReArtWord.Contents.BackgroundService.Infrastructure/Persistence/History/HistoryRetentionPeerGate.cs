using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>参加中の全実体が新しい履歴境界を理解してから共有本文を整理します。</summary>
public sealed class HistoryRetentionPeerGate(PeerCluster cluster)
{
    private readonly HashSet<(string Peer, Guid Instance)> confirmed = [];

    /// <summary>未対応や応答未確認を対応済みと推測しません。</summary>
    /// <returns>現在参加中の全Peerが対応しているかです。</returns>
    public async Task<bool> ReadyAsync(CancellationToken cancellationToken = default)
    {
        PeerInfo[] peers = cluster.GetPeers().Where(peer => peer.InstanceId != cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)).ToArray();
        confirmed.RemoveWhere(key => !peers.Any(peer => string.Equals(peer.PeerId, key.Peer, StringComparison.Ordinal) && peer.InstanceId == key.Instance));
        foreach (PeerInfo peer in peers)
        {
            if (confirmed.Contains((peer.PeerId, peer.InstanceId)))
            {
                continue;
            }
            if (!await SupportsAsync(peer, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            confirmed.Add((peer.PeerId, peer.InstanceId));
        }
        return true;
    }

    /// <summary>一つの実体の能力確認にだけ期限を与え、待機資源を次のPeerへ持ち越しません。</summary>
    /// <returns>保持境界を理解する確認が取れたかです。</returns>
    private async Task<bool> SupportsAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            WireMessage? response = await cluster.ExchangeAsync("sync.capabilities", peer.PeerId, peer.InstanceId, JsonSerializer.SerializeToElement(Guid.Empty), deadline.Token).ConfigureAwait(false);
            return response?.Body.Deserialize<SyncResponse>()?.HistoryRetention == true;
        }
        catch (Exception exception) when (SyncFailureException.IsExpected(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }
}
