using System.Text.Json;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>選択済みNIC上の実healthと既存のUpdater配送を使います。</summary>
internal sealed class FleetUpdateRemote(ObserverConnection connection) : IFleetUpdateRemote
{
    private static readonly JsonSerializerOptions healthOptions = new(ContractJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };

    /// <summary>更新対象Coreが保持する所属集合を読み、停止中のメンバーを分母から落としません。</summary>
    /// <returns>この実体で確認した所属IDです。</returns>
    // 抑制理由: 過半数の判定前に、一回の応答として所属集合全体を固定する必要があります。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW160", Justification = "所属集合を一回のsnapshotで検証してから停止可否を判定します。")]
    public async Task<IReadOnlyList<string>?> MembersAsync(FleetUpdateTarget target, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            UdpObserver? observer = connection.FindObserver(target.ClusterId, target.Peer.PeerId);
            if (observer is null || observer.CaptureBindingStatus().Binding != target.Binding)
            {
                return null;
            }
            ManagementReply reply = await observer.InspectAsync(new(Guid.NewGuid(), target.ClusterId, target.Peer.PeerId, target.Peer.InstanceId,
                DateTimeOffset.UtcNow.AddSeconds(4), "status", null), timeout.Token).ConfigureAwait(false);
            Membership? status = reply.Value?.Json.Deserialize<Membership>(healthOptions);
            if (reply.Outcome != EOutcome.Succeeded || status?.InstanceId != target.Peer.InstanceId || status.Peers is null
                || !string.Equals(status.ClusterId, target.ClusterId, StringComparison.Ordinal) || !string.Equals(status.PeerId, target.Peer.PeerId, StringComparison.Ordinal)
                || !status.Peers.Any(peer => string.Equals(peer.PeerId, target.Peer.PeerId, StringComparison.Ordinal)))
            {
                return null;
            }
            return status.Peers.Select(peer => peer.PeerId).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>別host・曖昧な実体・変更されたNICを正常な残存ピアとして扱いません。</summary>
    /// <returns>今回直接確認した稼働状態です。</returns>
    public async Task<FleetPeerObservation?> ObserveAsync(FleetUpdateTarget target, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            PeerInfo[] peers = connection.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, target.ClusterId, StringComparison.Ordinal))
                .SelectMany(cluster => cluster.Peers).Where(peer => string.Equals(peer.PeerId, target.Peer.PeerId, StringComparison.Ordinal)).ToArray();
            if (peers.Length != 1 || !string.Equals(peers[0].HostId, target.Peer.HostId, StringComparison.Ordinal)
                || TimeSpan.FromSeconds(15) < DateTimeOffset.UtcNow - peers[0].LastSeen)
            {
                return null;
            }
            UdpObserver? observer = connection.FindObserver(target.ClusterId, target.Peer.PeerId);
            if (observer is null || observer.CaptureBindingStatus().Binding != target.Binding)
            {
                return null;
            }
            PeerInfo peer = peers[0];
            ManagementReply reply = await observer.InspectAsync(new(Guid.NewGuid(), target.ClusterId, peer.PeerId, peer.InstanceId,
                DateTimeOffset.UtcNow.AddSeconds(4), "health", null), timeout.Token).ConfigureAwait(false);
            Health? health = reply.Value?.Json.Deserialize<Health>(healthOptions);
            if (reply.Outcome != EOutcome.Succeeded || health?.InstanceId != peer.InstanceId
                || !string.Equals(health.PeerId, peer.PeerId, StringComparison.Ordinal) || !string.Equals(health.ClusterId, target.ClusterId, StringComparison.Ordinal))
            {
                return null;
            }
            return new(peer, health.Status == EPeerStatus.Ready && health.Maintenance is null, health.BuildVersion);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>送信元のCoreの有無にかかわらず、TCP転送を優先します。</summary>
    /// <returns>開始時のNICと実体へ固定した配送です。</returns>
    public IUpdatePeerTransport Create(ClusterUpdateRequest request, FleetUpdateTarget target)
    {
        PeerInfo? source = connection.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, target.ClusterId, StringComparison.Ordinal))
            .SelectMany(cluster => cluster.Peers).FirstOrDefault(peer => string.Equals(peer.Address, target.Binding.BindAddress, StringComparison.Ordinal));
        ClusterUpdateJob job = new(request, new Dictionary<string, Guid>(StringComparer.Ordinal) { [target.Peer.PeerId] = target.Peer.InstanceId },
            target.Binding, target.ClusterId, string.Empty, "json", source?.PeerId ?? "clienthost");
        return new UdpUpdatePeerTransport(job);
    }

    /// <summary>稼働判定に必要なhealth項目だけを読みます。</summary>
    private sealed record Health(string ClusterId, string PeerId, Guid InstanceId, EPeerStatus Status, string? BuildVersion, JsonElement? Maintenance);

    /// <summary>過半数の分母を取得するための既存status契約です。</summary>
    private sealed record Membership(string ClusterId, string PeerId, Guid InstanceId, IReadOnlyList<PeerInfo> Peers);
}
