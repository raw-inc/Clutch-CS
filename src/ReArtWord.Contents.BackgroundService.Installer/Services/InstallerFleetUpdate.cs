using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>Installerが選んだ実体と配布物を固定し、共通のFleet処理で一台ずつ更新します。</summary>
internal static class InstallerFleetUpdate
{
    /// <summary>開始前と各工程を保存し、未確認の更新を再送しません。</summary>
    /// <returns>選択した全対象の確認結果です。</returns>
    // 抑制理由: 作成した監視処理はリストへ所有権を渡し、finallyで全件の終了を待ちます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "作成済みの全監視をfinallyで非同期解放します。")]
    internal static async Task<FleetUpdateStatus> ExecuteAsync(IReadOnlyList<ObservedTarget> targets, string payload,
        IProgress<OperationProgress> progress, CancellationToken cancellationToken = default)
    {
        FleetUpdateTarget[] selected = targets.Select(target => new FleetUpdateTarget(target.ClusterId,
            target.Peer ?? throw new InvalidDataException("Select observed peers for Fleet Update."), target.Binding))
            .OrderBy(target => target.Peer.HostId, StringComparer.Ordinal).ThenBy(target => target.ClusterId, StringComparer.Ordinal)
            .ThenBy(target => target.Peer.PeerId, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0 || selected.DistinctBy(target => (target.ClusterId, target.Peer.PeerId)).Count() != selected.Length)
        {
            throw new InvalidDataException("Select a unique route for every Fleet Update target.");
        }
        ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(payload, cancellationToken).ConfigureAwait(false);
        await ReleaseVerifier.VerifyAsync(manifest, payload, cancellationToken).ConfigureAwait(false);
        FleetUpdateRequest request = new(Guid.NewGuid(), new(manifest, payload, AllowWhileActive: true, MaximumParallelism: 1), selected);
        FleetUpdateStatus latest = new(request, false, EOutcome.Unknown,
            selected.Select(target => new FleetUpdatePeerProgress(target, "Queued", [])).ToArray(), DateTimeOffset.UtcNow);
        string directory = SetupWorkspace.DirectoryFor(request.OperationId);
        Directory.CreateDirectory(directory);
        string journal = Path.Combine(directory, "fleet.json");
        await UpdateProgressFile.WriteAsync(journal, latest, cancellationToken).ConfigureAwait(false);
        Dictionary<ClientNetworkBinding, UdpObserver> observers = [];
        try
        {
            foreach (ClientNetworkBinding binding in selected.Select(target => target.Binding).Distinct())
            {
                UdpObserver observer = new(new ObserverOptions("installer-fleet-" + request.OperationId.ToString("N"), binding));
                observers.Add(binding, observer);
                await observer.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            await WaitForTargetsAsync(selected, observers, cancellationToken).ConfigureAwait(false);
            UdpFleetUpdateRemote remote = new((cluster, peer) =>
            {
                ClientNetworkBinding[] bindings = selected.Where(target => string.Equals(target.ClusterId, cluster, StringComparison.Ordinal))
                    .Select(target => target.Binding).Distinct().ToArray();
                return ObserverRouting.Find(bindings.Select(binding => observers[binding]), cluster, peer);
            }, () => observers.Values.SelectMany(observer => observer.GetClusters()).ToArray());
            return await new FleetUpdateRunner(remote).RunAsync(latest, async (value, token) =>
            {
                await UpdateProgressFile.WriteAsync(journal, value, token).ConfigureAwait(false);
                latest = value;
                FleetUpdatePeerProgress? current = value.Peers.FirstOrDefault(peer => peer.State is "Preparing" or "Updating" or "Retrying");
                string? target = current?.Target.ClusterId + " / " + current?.Target.Peer.PeerId;
                string stage = current?.State switch { "Updating" => "update-apply", _ => "update-check" };
                progress.Report(new(stage, value.Peers.Count(peer => peer.State is "Completed" or "Deferred" or "Failed"), selected.Length, target));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            // 停止要求の前後を保存状態で区別し、確認していない副作用を失敗と断定しません。
            latest = latest with
            {
                Completed = true, Outcome = EOutcome.Unknown, UpdatedAt = DateTimeOffset.UtcNow,
                Reason = "Fleet Update was interrupted. Inspect the saved operation before starting another update.",
                Peers = latest.Peers.Select(peer => peer.State switch
                {
                    "Completed" or "Deferred" or "Failed" => peer,
                    "Updating" or "Unconfirmed" => peer with { State = "Unconfirmed" },
                    _ => peer with { State = "Deferred" },
                }).ToArray(),
            };
            await UpdateProgressFile.WriteAsync(journal, latest, CancellationToken.None).ConfigureAwait(false);
            return latest;
        }
        finally
        {
            foreach (UdpObserver observer in observers.Values)
            {
                await ((IAsyncDisposable)observer).DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>開始時の実体を観測してから、稼働確認と配送に進みます。</summary>
    /// <returns>観測完了です。未観測なら更新を開始しません。</returns>
    private static async Task WaitForTargetsAsync(IReadOnlyList<FleetUpdateTarget> targets, Dictionary<ClientNetworkBinding, UdpObserver> observers,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (targets.Any(target => !observers[target.Binding].GetClusters().Where(cluster => string.Equals(cluster.ClusterId, target.ClusterId, StringComparison.Ordinal))
            .SelectMany(cluster => cluster.Peers).Any(peer => peer.InstanceId == target.Peer.InstanceId)))
        {
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }
}
