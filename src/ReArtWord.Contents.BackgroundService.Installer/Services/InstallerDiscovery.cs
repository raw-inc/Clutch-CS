using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>選択した一つのNICでクラスタ通知を受動受信します。</summary>
internal static class InstallerDiscovery
{
    /// <summary>経路を勝手に変更せず、観測できた実体を返します。</summary>
    /// <returns>クラスタと実体で重複を除いた観測結果です。</returns>
    internal static async Task<IReadOnlyList<ObservedTarget>> ScanAsync(ClientNetworkBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ClientNetworkBinding discovery = binding with { Port = ManagementTransport.Datagrams.DiscoveryProtocol.Port };
        UdpObserver observer = new(new ObserverOptions("installer-" + Guid.NewGuid().ToString("N"), discovery) { IncludeDiscoveryAnnouncements = true });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        await observer.StartAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(InstallationDefaults.DiscoveryWindow, cancellationToken).ConfigureAwait(false);
        if (!observer.CaptureBindingStatus().Listening)
        {
            throw new IOException("The selected network is unavailable: " + binding.BindAddress);
        }
        return observer.GetClusters().SelectMany(cluster => cluster.Peers.Select(peer => new ObservedTarget(cluster.ClusterId, peer.PeerId, binding with { Port = peer.Port }, peer))).ToArray();
    }
}
