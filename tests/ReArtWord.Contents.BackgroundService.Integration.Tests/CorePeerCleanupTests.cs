using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>診断保存に失敗しても、試験用Coreの監視資源を解放します。</summary>
public sealed class CorePeerCleanupTests
{
    /// <summary>設定を保存できなかった構成が、管理通信用のポートを占有しません。</summary>
    /// <returns>構築失敗後にも同じポートを確保できます。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationWriteFailureDoesNotKeepThePortAsync(bool rolling)
    {
        string root = Path.GetFullPath("../../../../../.work/integration", AppContext.BaseDirectory);
        string cluster = Guid.NewGuid().ToString("N"), peerId = "failed-configuration";
        string blocked = Path.Combine(root, cluster, peerId, "contents.toml");
        Directory.CreateDirectory(blocked);
        int port = CorePeer.AvailablePort();
        IAsyncDisposable? peer = null;
        try
        {
            Exception? failure = Record.Exception(() =>
            {
                if (rolling)
                {
                    ReleaseManifest manifest = new("fixture", "win-x64", "Clutch-CS.CoreService.exe", [], []);
                    peer = new RollingUpdatePeer(root, cluster, peerId, port, root, manifest, true);
                }
                else
                {
                    peer = new CorePeer(cluster, peerId, port, true);
                }
            });
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        finally
        {
            if (peer is not null)
            {
                await peer.DisposeAsync().ConfigureAwait(true);
            }
            Directory.Delete(blocked);
        }
    }

    /// <summary>起動不能なCoreの診断を書けない場合も、破棄済みの監視を再開しません。</summary>
    /// <returns>保存失敗の通知と監視の終了確認です。</returns>
    // 抑制理由: DisposeAsync自体の失敗を検証し、finallyでも所有するPeerの回収を試みます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "破棄の例外を明示的に検証し、finallyで残った試験資源を回収します。")]
    [Fact]
    public async Task DiagnosticWriteFailureStillDisposesTheObserverAsync()
    {
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "failed-start", CorePeer.AvailablePort(), true);
        string diagnostic = Path.Combine(peer.DirectoryPath, "startup-error.txt");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(peer.DirectoryPath, "contents.toml"), "[cluster").ConfigureAwait(true);
            await Assert.ThrowsAsync<IOException>(() => peer.StartAsync()).ConfigureAwait(true);
            Directory.CreateDirectory(diagnostic);
            Exception? failure = await Record.ExceptionAsync(() => ((IAsyncDisposable)peer).DisposeAsync().AsTask()).ConfigureAwait(true);
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => peer.StartAsync()).ConfigureAwait(true);
        }
        finally
        {
            try
            {
                if (Directory.Exists(diagnostic))
                {
                    Directory.Delete(diagnostic);
                }
            }
            finally
            {
                try
                {
                    await ((IAsyncDisposable)peer).DisposeAsync().ConfigureAwait(true);
                }
                catch (ObjectDisposedException)
                {
                    // 検証対象の破棄が完了済みの場合だけ、後始末の重複を許容します。
                }
            }
        }
    }
}
