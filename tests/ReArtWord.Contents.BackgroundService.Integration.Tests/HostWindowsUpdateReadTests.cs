using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WindowsUpdate;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>管理通信を経由した更新ポリシーの読取りだけを確認します。</summary>
public sealed class HostWindowsUpdateReadTests
{
    /// <summary>更新を開始せず、Coreが確認した対応エディションとポリシーを取得します。</summary>
    /// <returns>実Coreとの通信結果です。</returns>
    [WindowsFact]
    public async Task ReadsWindowsUpdatePolicyThroughTheCoreAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "update-policy", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        DeviceSnapshot snapshot = await peer.GetAsync<DeviceSnapshot>("host-manager.read", new DeviceReadRequest(EDeviceArea.WindowsUpdate)).ConfigureAwait(true);
        DeviceWindowsUpdateState state = DeviceSnapshots.Read<DeviceWindowsUpdateState>(snapshot.Data);
        Assert.NotEmpty(state.Edition);
        Assert.NotEmpty(state.Deadlines);
        Assert.Equal(64, snapshot.Revision.Length);
    }
}
