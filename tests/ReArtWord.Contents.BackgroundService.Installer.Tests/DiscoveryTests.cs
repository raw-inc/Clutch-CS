using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>実UDP通知から候補と接続portを取得し、NIC変更時に古い対象を破棄します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class DiscoveryTests
{
    /// <summary>同名・別portのクラスターを区別し、参加先portを通知から引き継ぎます。</summary>
    /// <returns>受動発見と入力固定の検証完了です。</returns>
    [Fact]
    public async Task PassiveDiscoveryUsesAdvertisedPortsAndOneExplicitNetwork()
    {
        InstallationViewModel settings = new() { Accepted = true, LicenseRead = true };
        DiscoveryViewModel discovery = new(settings);
        Assert.Null(discovery.SelectedNetwork);
        Assert.Throws<InvalidDataException>(discovery.CaptureNetwork);
        NetworkChoice selected = discovery.Networks.Single(network => string.Equals(network.Binding.BindAddress, "127.0.0.1", StringComparison.Ordinal));
        discovery.SelectedNetwork = selected;
        ClientNetworkBinding binding = selected.Binding;
        using UdpClient firstReservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient secondReservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int firstPort = ((IPEndPoint)firstReservation.Client.LocalEndPoint!).Port;
        int secondPort = ((IPEndPoint)secondReservation.Client.LocalEndPoint!).Port;
        string cluster = "discovery-" + Guid.NewGuid().ToString("N");
        UdpTransport publisher = new(new DatagramTransportOptions("fixture", binding.BindAddress, binding.BroadcastAddress, DiscoveryProtocol.Port), Guid.NewGuid(), TimeProvider.System);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable publisherLifetime = publisher.ConfigureAwait(false);
        publisher.Initialize(binding.InterfaceId);
        using CancellationTokenSource publishing = new();
        Task announcements = PublishAsync(publisher, cluster, [firstPort, secondPort], publishing.Token);
        try
        {
            await discovery.ScanAsync();
            Assert.True(discovery.SearchCompleted);
            Assert.False(settings.NewCluster);
            Assert.Equal(discovery.Clusters.First(), discovery.SelectedCluster);
            ClusterChoice[] found = discovery.Clusters.Where(choice => string.Equals(choice.Name, cluster, StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, found.Length);
            ClusterChoice chosen = found.Single(choice => choice.Port == secondPort);
            discovery.SelectedCluster = chosen;
            await discovery.ScanAsync();
            Assert.Equal(chosen, discovery.SelectedCluster);
            settings.Port = -1;
            discovery.CaptureTargets();
            Assert.False(settings.NewCluster);
            Assert.Single(settings.Networks);
            Assert.Equal(secondPort, settings.Settings().Network.Port);
            settings.NewCluster = true;
            Assert.Throws<InvalidDataException>(() => settings.Settings());
            settings.Port = firstPort;
            Assert.Equal(firstPort, settings.Settings().Network.Port);

            discovery.SelectedNetwork = null;
            Assert.Null(discovery.SelectedCluster);
            Assert.Empty(discovery.Clusters);
            Assert.Empty(discovery.Targets);
            Assert.Empty(settings.Networks);
            Assert.Empty(settings.Observed);
            Assert.Empty(settings.Selected);
            Assert.False(discovery.SearchCompleted);
            Assert.Throws<InvalidDataException>(discovery.CaptureTargets);
        }
        finally
        {
            await publishing.CancelAsync();
            await announcements.ConfigureAwait(true);
        }
    }

    /// <summary>候補なしの再検索では古い参加先を破棄し、新規作成へ切り替えます。</summary>
    /// <returns>実ネットワークの他の試験通知に依存しない選択検証です。</returns>
    [Fact]
    public async Task EmptySearchSelectsNewClusterAndDiscardsStaleSelection()
    {
        InstallationViewModel settings = new();
        DiscoveryViewModel discovery = new(settings, (_, _) => Task.FromResult<IReadOnlyList<Services.ObservedTarget>>([]));
        discovery.SelectedNetwork = discovery.Networks.Single(network => string.Equals(network.Binding.BindAddress, "127.0.0.1", StringComparison.Ordinal));
        ClusterChoice previous = new("gone", 50123);
        discovery.Clusters.Add(previous);
        discovery.SelectedCluster = previous;
        Assert.False(settings.NewCluster);
        await discovery.ScanAsync();
        Assert.True(discovery.SearchCompleted);
        Assert.Empty(discovery.Clusters);
        Assert.Null(discovery.SelectedCluster);
        Assert.True(settings.NewCluster);
    }

    /// <summary>共有状態に参加せず、試験用の存在通知だけをloopbackへ送ります。</summary>
    /// <returns>試験終了までの通知です。</returns>
    private static async Task PublishAsync(UdpTransport publisher, string cluster, int[] ports, CancellationToken cancellationToken)
    {
        Guid[] instances = ports.Select(_ => Guid.NewGuid()).ToArray();
        long sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                sequence++;
                for (int index = 0; index < ports.Length; index++)
                {
                    PeerInfo peer = new("peer-" + index, instances[index], "fixture", "127.0.0.1", ports[index], EPeerStatus.Ready, 1000, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    PeerAnnouncement announcement = new(peer, string.Empty, peer.PeerId, null, sequence);
                    WireMessage message = new(Guid.NewGuid(), cluster, peer.PeerId, peer.InstanceId, DiscoveryProtocol.AnnouncementKind, null, null, JsonSerializer.SerializeToElement(announcement));
                    await publisher.SendAsync(message, cancellationToken).ConfigureAwait(false);
                }
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }
}
