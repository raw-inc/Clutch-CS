using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Login;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Coreの管理通信でユーザー候補と公開鍵だけを取得します。</summary>
public sealed class HostLoginReadTests
{
    /// <summary>パスワードの認証や保存を行わず、同じCoreで公開鍵と内容版を維持します。</summary>
    /// <returns>秘密値を含まない設定の照会です。</returns>
    [Fact]
    public async Task ReadsStableLoginSettingsAndAProcessOwnedPublicKeyAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "login-peer", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        DeviceSnapshot first = await peer.GetAsync<DeviceSnapshot>("host-manager.read", new DeviceReadRequest(EDeviceArea.Login)).ConfigureAwait(true);
        DeviceSnapshot second = await peer.GetAsync<DeviceSnapshot>("host-manager.read", new DeviceReadRequest(EDeviceArea.Login)).ConfigureAwait(true);
        DeviceLoginState state = DeviceSnapshots.Read<DeviceLoginState>(first.Data);
        Assert.NotEmpty(state.Users);
        Assert.NotNull(state.EncryptionKey);
        Assert.Equal(64, state.EncryptionKey.Id.Length);
        Assert.NotEmpty(Convert.FromBase64String(state.EncryptionKey.Spki));
        Assert.Equal(first.Revision, second.Revision);
    }
}
