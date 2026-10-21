using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Coreの登録処理が終わった後も、NIC設定サービスを読み取れることを確認します。</summary>
public sealed class HostWakeReadTests
{
    /// <summary>NIC・電源・レジストリを変更せず、実起動後の能力と繰返し読取りを検証します。</summary>
    /// <returns>管理通信と実OS一覧の確認です。</returns>
    [Fact]
    public async Task WakeSettingsRemainAvailableAfterCoreStartup()
    {
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "wake-read", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        DeviceCapabilities capabilities = await peer.GetAsync<DeviceCapabilities>("host-manager.capabilities").ConfigureAwait(true);
        Assert.Contains(EDeviceArea.WakeOnLan, capabilities.Areas);
        DeviceSnapshot first = await peer.GetAsync<DeviceSnapshot>("host-manager.read", new DeviceReadRequest(EDeviceArea.WakeOnLan, "refresh")).ConfigureAwait(true);
        DeviceWakeState state = DeviceSnapshots.Read<DeviceWakeState>(first.Data);
        state.Validate();
        Assert.NotEmpty(state.Inventory.Interfaces);
        DeviceSnapshot second = await peer.GetAsync<DeviceSnapshot>("host-manager.read", new DeviceReadRequest(EDeviceArea.WakeOnLan)).ConfigureAwait(true);
        Assert.Equal(first.Revision, second.Revision);
        Assert.True(state.Selection.Matches(DeviceSnapshots.Read<DeviceWakeState>(second.Data).Selection));
    }
}
