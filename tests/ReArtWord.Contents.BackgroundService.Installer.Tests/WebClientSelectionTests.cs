using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>Coreと管理画面の独立した選択と共有配置の重複除去を確認します。</summary>
[Collection(nameof(InstallerTestApp))]
public sealed class WebClientSelectionTests
{
    /// <summary>Webクライアントだけの選択が可能で、NIC変更時には両方の選択を無効にします。</summary>
    /// <returns>入力固定と確認画面の検証です。</returns>
    [Fact]
    public async Task ClientsAreSelectedIndependentlyAndSharedCopiesAppearOnce()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        WebClientInstallation local = new(Guid.NewGuid(), Path.Combine(fixture.Root, "local-client"), "Clutch-CS.ClientHost.exe", 55301, true, true);
        WebClientInstallation remote = local with { InstallationId = Guid.NewGuid(), Root = Path.Combine(fixture.Root, "remote-client") };
        InstallationViewModel settings = new() { Operation = EInstallerOperation.Update };
        ObservedTarget[] peers = [Target("local", "this-host", "127.0.0.1", local), Target("first", "other-host", "192.0.2.20", remote), Target("second", "other-host", "192.0.2.20", remote)];
        DiscoveryViewModel discovery = new(settings, (_, _) => Task.FromResult<IReadOnlyList<ObservedTarget>>(peers), _ => Task.FromResult<WebClientInstallation?>(local));
        discovery.SelectedNetwork = discovery.Networks.Single(network => string.Equals(network.Binding.BindAddress, "127.0.0.1", StringComparison.Ordinal));
        await discovery.ScanAsync();
        Assert.Equal(2, discovery.WebClients.Count);
        discovery.Scope = (int)ETargetScope.Peers;
        WebClientChoiceViewModel selected = discovery.WebClients.Single(choice => choice.Target.Installation == remote);
        selected.Selected = true;
        discovery.CaptureTargets();
        Assert.Empty(settings.Selected);
        Assert.Equal(remote, Assert.Single(settings.SelectedWebClients).Installation);
        Assert.Contains("other-host", WizardSummary.Create(settings), StringComparison.Ordinal);
        Assert.DoesNotContain(local.Root, WizardSummary.Create(settings), StringComparison.Ordinal);

        discovery.Targets.Single(choice => string.Equals(choice.Target.PeerId, peers[2].PeerId, StringComparison.Ordinal)).Selected = true;
        discovery.CaptureTargets();
        Assert.Equal(peers[2], Assert.Single(settings.SelectedWebClients).Core);
        selected.Selected = false;
        discovery.CaptureTargets();
        Assert.Single(settings.Selected);
        Assert.Empty(settings.SelectedWebClients);
        selected.Selected = true;
        discovery.SelectedNetwork = null;
        Assert.Empty(settings.Selected);
        Assert.Empty(settings.SelectedWebClients);
        Assert.Empty(discovery.WebClients);

        ObservedTarget Target(string id, string host, string address, WebClientInstallation client)
        {
            ClientNetworkBinding binding = fixture.Settings.Network;
            PeerInfo peer = new(id, Guid.NewGuid(), host, address, binding.Port, EPeerStatus.Ready, 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            InstallationRecord installation = new(Guid.NewGuid(), fixture.Settings with { PeerId = id }, "fixture", "core", "updater", "client", DateTimeOffset.UtcNow, client);
            return new(fixture.Settings.ClusterId, id, binding, peer, installation);
        }
    }
}
