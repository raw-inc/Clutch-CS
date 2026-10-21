using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;

/// <summary>他Peerの観測状態と担当の変化を通知し、ハートビートごとの再通知を避けます。</summary>
public sealed class PeerEventObservations(IPeerDirectory peers, IWorkflowEvents events)
{
    private readonly Dictionary<(string Peer, Guid Instance), PeerObservation> previous = new();
    private string? coordinator;

    /// <summary>今回観測できた変更だけを、その観測元のPeerから発行します。</summary>
    /// <returns>状態変化の通知試行。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        PeerInfo[] current = peers.GetPeers().Where(peer => peer.InstanceId != peers.LocalInstanceId).ToArray();
        HashSet<(string Peer, Guid Instance)> present = [];
        foreach (PeerInfo peer in current)
        {
            (string Peer, Guid Instance) key = (peer.PeerId, peer.InstanceId);
            present.Add(key);
            PeerObservation stable = new(peer.PeerId, peer.InstanceId, peer.HostId, peer.Address, peer.Port, peer.Status, peer.Priority);
            PeerObservation? before = previous.GetValueOrDefault(key);
            if (before != stable)
            {
                previous[key] = stable;
                await PublishAsync("peer.changed", new { previous = before, current = stable }, cancellationToken).ConfigureAwait(false);
            }
        }
        foreach ((string Peer, Guid Instance) key in previous.Keys.Where(key => !present.Contains(key)).ToArray())
        {
            PeerObservation before = previous[key];
            previous.Remove(key);
            await PublishAsync("peer.changed", new { previous = before, current = (PeerObservation?)null }, cancellationToken).ConfigureAwait(false);
        }
        string? next = peers.CoordinatorId;
        if (!string.Equals(coordinator, next, StringComparison.Ordinal))
        {
            string? before = coordinator;
            coordinator = next;
            await PublishAsync("cluster.coordinatorChanged", new { previous = before, current = next }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>観測対象と観測元を区別した事象を作ります。</summary>
    /// <returns>発行試行の完了。</returns>
    private ValueTask PublishAsync<T>(string name, T data, CancellationToken cancellationToken = default)
        => WorkflowEventBoundary.PublishAsync(events, new WorkflowEvent(Guid.NewGuid(), name, DateTimeOffset.UtcNow, peers.LocalPeerId,
            Contracts.Serialization.ContractJson.ToValue(data), PeerInstanceId: peers.LocalInstanceId), cancellationToken);

    /// <summary>変化の通知に使用する観測項目。ハートビート時刻や容量統計は含めません。</summary>
    /// <param name="PeerId">観測対象Peer。</param>
    /// <param name="InstanceId">観測対象の起動ID。</param>
    /// <param name="HostId">所属PC。</param>
    /// <param name="Address">通信先アドレス。</param>
    /// <param name="Port">通信先ポート。</param>
    /// <param name="Status">観測した参加状態。</param>
    /// <param name="Priority">設定された実行優先度。</param>
    private sealed record PeerObservation(string PeerId, Guid InstanceId, string HostId, string Address, int Port, EPeerStatus Status, int Priority);
}
