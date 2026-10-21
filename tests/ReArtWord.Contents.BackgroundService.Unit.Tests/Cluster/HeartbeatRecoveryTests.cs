using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>内部タイムアウトや保存失敗で生存通知が永久停止しないことを実UDPで確認します。</summary>
public sealed class HeartbeatRecoveryTests
{
    /// <summary>参加不能を通知し続け、条件回復後に同じ実体で再参加します。</summary>
    /// <returns>障害前・継続中・回復後の実通信です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalFailureDoesNotStopAnnouncements(bool databaseFailure)
    {
        using MediaTestFiles files = new();
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string cluster = Guid.NewGuid().ToString("N");
        await using PeerCluster first = Peer(cluster, "first", port, true, Path.Combine(files.Root, "first.db"));
        await first.StartAsync();
        await using PeerCluster second = Peer(cluster, "second", port, false, Path.Combine(files.Root, "second.db"));
        await second.StartAsync();
        await WaitAsync(() => second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow;
        first.ReadinessCheck = _ => throw Failure(databaseFailure);
        await WaitAsync(() => second.GetPeers().Any(peer => peer.InstanceId == first.LocalInstanceId && peer.Status == EPeerStatus.Unavailable && failedAt < peer.LastSeen));
        DateTimeOffset observed = second.GetPeers().Single(peer => peer.InstanceId == first.LocalInstanceId).LastSeen;
        await WaitAsync(() => second.GetPeers().Any(peer => peer.InstanceId == first.LocalInstanceId && peer.Status == EPeerStatus.Unavailable && observed < peer.LastSeen));
        first.ReadinessCheck = _ => ValueTask.FromResult(true);
        await WaitAsync(() => second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2);
    }

    /// <summary>親トークンとは独立した失敗を作ります。</summary>
    /// <returns>内部で発生する既知の例外です。</returns>
    private static Exception Failure(bool databaseFailure)
    {
        if (databaseFailure)
        {
            return new SqliteException("Simulated busy storage", 5);
        }
        return new OperationCanceledException("Simulated local timeout");
    }

    /// <summary>実UDPを使う小さな独立Peerです。</summary>
    /// <returns>未開始のPeerです。</returns>
    private static PeerCluster Peer(string cluster, string id, int port, bool fresh, string path)
        => new(new ClusterOptions(cluster, id, id, "127.0.0.1", "127.255.255.255", port, 1, fresh, Path.GetDirectoryName(path)!)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(100), PeerTimeout = TimeSpan.FromSeconds(2), RequestTimeout = TimeSpan.FromSeconds(4) }, new SqliteRepository(path))
        { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>条件確認を有限時間で待ちます。</summary>
    /// <returns>条件成立までの待機です。</returns>
    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token).ConfigureAwait(false);
        }
    }
}
