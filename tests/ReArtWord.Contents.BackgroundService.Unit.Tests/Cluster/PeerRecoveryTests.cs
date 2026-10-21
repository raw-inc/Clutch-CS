using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>後から整う実Ready条件と新規作成の明示許可を区別します。</summary>
public sealed class PeerRecoveryTests
{
    /// <summary>明示新規の場合だけ、遅れてReady条件が揃っても単独開始できます。</summary>
    /// <returns>実ハートビートの検証完了。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelayedReadinessRequiresExplicitCreationOrTrustedState(bool initialize)
    {
        int port;
        using (UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        }
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), id, "state.db"));
        bool ready = false;
        PeerCluster peer = new(new ClusterOptions(id, "one", "host", "127.0.0.1", "127.255.255.255", port, 1, initialize, Path.GetTempPath()) { PeerTimeout = TimeSpan.FromMilliseconds(200), HeartbeatInterval = TimeSpan.FromMilliseconds(25) }, repository)
        {
            ReadinessCheck = token => ValueTask.FromResult(Volatile.Read(ref ready)),
        };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = peer.ConfigureAwait(false);
        await peer.StartAsync();
        Assert.NotEqual(EPeerStatus.Ready, Assert.Single(peer.GetPeers()).Status);
        Volatile.Write(ref ready, true);
        if (initialize)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            while (peer.GetPeers().Single().Status != EPeerStatus.Ready)
            {
                await Task.Delay(20, timeout.Token);
            }
            Assert.Equal("trusted", (await repository.ReadAsync("cluster:" + id, "one"))!.Value!.GetString());
        }
        else
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.NotEqual(EPeerStatus.Ready, Assert.Single(peer.GetPeers()).Status);
        }
    }
}
