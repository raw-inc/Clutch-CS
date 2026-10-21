using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>中断後は元Updaterの結果だけを照会し、更新開始や新しい試行は発行しません。</summary>
internal sealed class FleetUpdateRecovery(IFleetUpdateRemote remote)
{
    /// <summary>確定した入替・復元の保留を解除し、未確認の結果を画面へ残します。</summary>
    /// <returns>再確認後の全体進捗です。</returns>
    internal async Task<FleetUpdateStatus> ReconcileAsync(FleetUpdateStatus status, CancellationToken cancellationToken = default)
    {
        List<FleetUpdatePeerProgress> peers = [];
        string[] terminal = ["Completed", "Deferred", "Failed"], notDispatched = ["Queued", "Preparing", "Retrying"];
        foreach (FleetUpdatePeerProgress peer in status.Peers)
        {
            if (terminal.Contains(peer.State, StringComparer.Ordinal))
            {
                peers.Add(peer);
                continue;
            }
            if (notDispatched.Contains(peer.State, StringComparer.Ordinal))
            {
                peers.Add(peer with { State = "Deferred", Reason = "This peer was not dispatched. Preview a new update to retry it." });
                continue;
            }
            peers.Add(await ReconcilePeerAsync(status.Request, peer, cancellationToken).ConfigureAwait(false));
        }
        string completed = "Completed", unconfirmed = "Unconfirmed";
        EOutcome outcome = EOutcome.Failed;
        if (peers.All(peer => string.Equals(peer.State, completed, StringComparison.Ordinal)))
        {
            outcome = EOutcome.Succeeded;
        }
        else
        {
            if (peers.Any(peer => string.Equals(peer.State, unconfirmed, StringComparison.Ordinal)))
            {
                outcome = EOutcome.Unknown;
            }
        }
        return status with { Peers = peers, Completed = true, Outcome = outcome, UpdatedAt = DateTimeOffset.UtcNow,
            Reason = outcome == EOutcome.Unknown ? "Some updater outcomes remain unconfirmed."
                                                 : null };
    }

    /// <summary>一つの元操作を照会し、確定結果がある場合だけ同じ保留を解除します。</summary>
    /// <returns>再確認できた一ピアの状態です。</returns>
    // 抑制理由: factoryが返す一試行のtransportはfinallyで非同期解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "transportはfinallyで非同期解放します。")]
    private async Task<FleetUpdatePeerProgress> ReconcilePeerAsync(FleetUpdateRequest request, FleetUpdatePeerProgress progress, CancellationToken cancellationToken = default)
    {
        if (progress.Attempts.Count == 0)
        {
            return progress with { State = "Unconfirmed", Reason = "The original update operation ID is missing." };
        }
        Guid operation = progress.Attempts[^1].OperationId;
        FleetUpdateTarget target = progress.Target;
        if (progress.Attempts[^1].PreviousInstanceId is { } previous)
        {
            target = target with { Peer = target.Peer with { InstanceId = previous } };
        }
        ClusterUpdateRequest input = new(operation, request.Release.Manifest, request.Release.PayloadRoot, [target.Peer.PeerId], EUpdateFailurePolicy.KeepCompatibleMixed,
            Variants: request.Release.Variants);
        IUpdatePeerTransport transport = remote.Create(input, target);
        try
        {
            UpdatePeerResult result = await transport.ReadAsync(operation, target.Peer.PeerId, cancellationToken).ConfigureAwait(false);
            if (result.OperationId != operation || !string.Equals(result.PeerId, target.Peer.PeerId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The updater replied for another operation.");
            }
            FleetUpdateAttempt[] attempts = progress.Attempts.ToArray();
            attempts[^1] = attempts[^1] with { Result = result };
            progress = progress with { Attempts = attempts };
            string missing = "update.operationNotFound";
            bool completed = result.Phase == EUpdatePhase.Completed && result.Outcome == EOutcome.Succeeded
                && result.InstanceId.HasValue && result.InstanceId != target.Peer.InstanceId;
            bool restored = FleetUpdateResult.IsRestored(result, target.Peer.InstanceId);
            bool notExecuted = result.Phase == EUpdatePhase.Failed && result.Outcome == EOutcome.NotExecuted && !string.Equals(result.Reason, missing, StringComparison.Ordinal);
            if (completed || restored || notExecuted)
            {
                await transport.ReleaseAsync(operation, target.Peer.PeerId, cancellationToken).ConfigureAwait(false);
                FleetPeerObservation? current = await remote.ObserveAsync(target, cancellationToken).ConfigureAwait(false);
                string? version = null;
                if (completed)
                {
                    string platform = await transport.GetPlatformAsync(target.Peer.PeerId, cancellationToken).ConfigureAwait(false);
                    version = ReleaseSelector.Select(input, platform).Manifest.ExpectedBuildVersion;
                }
                if (current is { Ready: true } && current.Peer.InstanceId == (result.InstanceId ?? target.Peer.InstanceId)
                    && (version is null || string.Equals(version, current.BuildVersion, StringComparison.Ordinal)))
                {
                    return progress with { State = completed ? "Completed"
                                                             : "Failed", Reason = result.Reason };
                }
            }
            return progress with { State = "Unconfirmed", Reason = "The original update or peer recovery remains unconfirmed." };
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return progress with { State = "Unconfirmed", Reason = "The original updater could not be confirmed." };
        }
        finally
        {
            if (transport is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
