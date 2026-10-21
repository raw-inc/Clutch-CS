using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>画面に提示する対象と配布方針を固定し、未確認の範囲を黙って省略しません。</summary>
internal sealed class FleetUpdatePlanner(ObserverConnection connection)
{
    /// <summary>発見中の全クラスタ・全ピアをhost順で提示します。</summary>
    /// <returns>開始前に確認する固定計画です。</returns>
    internal FleetUpdateRequest Create(FleetReleaseSelection release)
    {
        ValidateRelease(release);
        FleetUpdateTarget[] targets = connection.GetClusters().SelectMany(cluster => cluster.Peers.Select(peer =>
            new FleetUpdateTarget(cluster.ClusterId, peer, connection.FindObserver(cluster.ClusterId, peer.PeerId)?.CaptureBindingStatus().Binding
                ?? throw new InvalidOperationException("A peer route is ambiguous or unavailable."))))
            .OrderBy(target => string.Equals(target.Peer.Address, target.Binding.BindAddress, StringComparison.Ordinal))
            .ThenBy(target => target.Peer.Priority).ThenBy(target => target.Peer.HostId, StringComparer.Ordinal)
            .ThenBy(target => target.ClusterId, StringComparer.Ordinal).ToArray();
        FleetUpdateRequest request = new(Guid.NewGuid(), release, targets);
        Validate(request);
        return request;
    }

    /// <summary>開始時にも対象のhost・実体・NICがプレビューと一致することを確認します。</summary>
    internal void Validate(FleetUpdateRequest request)
    {
        ValidateRelease(request.Release);
        if (request.OperationId == Guid.Empty || request.Targets.Count == 0 || 256 < request.Targets.Count
            || request.Targets.Count != connection.GetClusters().Sum(cluster => cluster.Peers.Count)
            || request.Targets.DistinctBy(target => (target.ClusterId, target.Peer.PeerId)).Count() != request.Targets.Count)
        {
            throw new ArgumentException("Select between 1 and 256 unique peers.", nameof(request));
        }
        foreach (FleetUpdateTarget target in request.Targets)
        {
            Contracts.Cluster.PeerInfo[] matches = connection.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, target.ClusterId, StringComparison.Ordinal))
                .SelectMany(cluster => cluster.Peers).Where(peer => string.Equals(peer.PeerId, target.Peer.PeerId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].InstanceId != target.Peer.InstanceId
                || !string.Equals(matches[0].HostId, target.Peer.HostId, StringComparison.Ordinal)
                || connection.FindObserver(target.ClusterId, target.Peer.PeerId)?.CaptureBindingStatus().Binding != target.Binding)
            {
                throw new InvalidOperationException("The peer or network changed. Preview the release again.");
            }
        }
    }

    /// <summary>既存Updaterの契約と有限の試行・待機条件を検証します。</summary>
    internal static void ValidateRelease(FleetReleaseSelection release)
    {
        ArgumentNullException.ThrowIfNull(release);
        ClientUpdateLauncher.Validate(release);
        ArgumentOutOfRangeException.ThrowIfLessThan(release.MaximumParallelism, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(release.MaximumParallelism, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(release.MaximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(release.MaximumAttempts, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(release.PeerTimeoutMilliseconds, 1000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(release.PeerTimeoutMilliseconds, 3600000);
        ReleaseSelector.Validate(new(Guid.NewGuid(), release.Manifest, release.PayloadRoot, ["validation"], EUpdateFailurePolicy.KeepCompatibleMixed, Variants: release.Variants));
    }
}
