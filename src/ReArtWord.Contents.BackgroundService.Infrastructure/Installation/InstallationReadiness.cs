using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>DBを空にする直前の実同期元を確認します。</summary>
internal static class InstallationReadiness
{
    /// <summary>保持対象外のDBを誤って初期化せず、別のReady実体を照会します。</summary>
    /// <returns>同期元の確認完了です。</returns>
    internal static async Task ConfirmSurvivorAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        string config = Path.Combine(record.Settings.Root, "config", "contents.toml");
        CommonConfiguration configuration = CommonConfigurationReader.Read(await File.ReadAllTextAsync(config, cancellationToken).ConfigureAwait(false), Path.GetDirectoryName(config)!);
        string managedData = Path.Combine(record.Settings.Root, "data");
        if (!string.Equals(configuration.DataDirectory, managedData, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The database location was customized. Preserve it or restore it explicitly before resynchronizing.");
        }
        UdpObserver observer = new(new ObserverOptions("setup-survivor-" + Guid.NewGuid().ToString("N"), record.Settings.Network) { RequestTimeout = InstallationDefaults.RequestTimeout });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        await observer.StartAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(InstallationDefaults.DiscoveryWindow, cancellationToken).ConfigureAwait(false);
        foreach (Contracts.Cluster.PeerInfo peer in observer.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, record.Settings.ClusterId, StringComparison.Ordinal)).SelectMany(cluster => cluster.Peers)
            .Where(peer => peer.Status == Contracts.Cluster.EPeerStatus.Ready && !string.Equals(peer.PeerId, record.Settings.PeerId, StringComparison.Ordinal)))
        {
            ManagementReply health = await observer.InspectAsync(new ManagementCommand(Guid.NewGuid(), record.Settings.ClusterId, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.Add(InstallationDefaults.RequestTimeout), "health", null), cancellationToken).ConfigureAwait(false);
            string ready = "ready";
            if (health.Outcome == EOutcome.Succeeded && health.Value?.Json.TryGetProperty(ready, out System.Text.Json.JsonElement value) == true && value.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                return;
            }
        }
        throw new InvalidDataException("No other Ready peer could be confirmed. The existing database was retained.");
    }
}
