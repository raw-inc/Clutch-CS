using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Firewall;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>実Windowsへの参照だけを行い、PowerShellと型付き契約の境界を確認します。</summary>
public sealed class DeviceWindowsReadTests
{
    /// <summary>NIC/Firewall一覧の各版が単一読取りと一致することを確認します。</summary>
    /// <returns>実設定を変更しない読取り確認です。</returns>
    [Fact]
    public async Task WindowsInventoryAndIndividualReadsUseTheSameRevisionAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using MediaTestFiles files = new();
        using DeviceSettingsPlatform platform = new(files.Root);
        DeviceSnapshot snapshot = await platform.ReadAsync(new(EDeviceArea.Network));
        DeviceNetworkState state = DeviceSnapshots.Read<DeviceNetworkState>(snapshot.Data);
        Assert.NotEmpty(state.Interfaces);
        DeviceNetworkInterface selected = state.Interfaces.FirstOrDefault(item => item.Physical) ?? state.Interfaces[0];
        DeviceSnapshot direct = await platform.ReadAsync(new(EDeviceArea.Network, selected.Id));
        Assert.Equal(selected.Id, DeviceSnapshots.Read<DeviceNetworkInterface>(direct.Data).Id);
        Assert.Equal(selected.Revision, direct.Revision);
        DeviceSnapshot firewall = await platform.ReadAsync(new(EDeviceArea.Firewall));
        DeviceFirewallState rules = DeviceSnapshots.Read<DeviceFirewallState>(firewall.Data);
        Assert.Equal(3, rules.Profiles.Count);
        DeviceFirewallRule rule = rules.Rules[0];
        Assert.NotNull(rule.DetailsHash);
        DeviceSnapshot ruleRead = await platform.ReadAsync(new(EDeviceArea.Firewall, "rule/" + rule.Id));
        Assert.Equal(rule.Revision, ruleRead.Revision);
    }
}
