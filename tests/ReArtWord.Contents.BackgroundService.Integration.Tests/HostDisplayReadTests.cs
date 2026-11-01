using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Display;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Coreの管理通信から対話コンソールの補助プロセスまで読取りを確認します。</summary>
public sealed class HostDisplayReadTests
{
    /// <summary>表示を変更せず、コンソールから取得した候補と個別の内容版を照合します。</summary>
    /// <returns>管理通信・pipe・実表示APIの読取り確認です。</returns>
    [WindowsFact]
    public async Task ReadsConnectedDisplaysThroughTheOwnedInteractiveWorkerAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "display-peer", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        DeviceCapabilities capabilities = await peer.GetAsync<DeviceCapabilities>("host-manager.capabilities").ConfigureAwait(true);
        Assert.Contains(EDeviceArea.Display, capabilities.Areas);
        DeviceSnapshot result = await peer.GetAsync<DeviceSnapshot>("host-manager.read", new DeviceReadRequest(EDeviceArea.Display)).ConfigureAwait(true);
        DeviceDisplayState state = DeviceSnapshots.Read<DeviceDisplayState>(result.Data);
        Assert.NotEmpty(state.Displays);
        foreach (DeviceDisplayInfo display in state.Displays)
        {
            Assert.True(0 < display.SessionId);
            Assert.NotEmpty(display.Modes);
            DeviceSnapshot single = await peer.GetAsync<DeviceSnapshot>("host-manager.read", new DeviceReadRequest(EDeviceArea.Display, display.Id)).ConfigureAwait(true);
            Assert.Equal(display.Revision, single.Revision);
            Assert.Equal(display.Current, DeviceSnapshots.Read<DeviceDisplayInfo>(single.Data).Current);
        }
    }
}
