using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>信頼済みの単独復旧でも、時計の補正で参加者の発見猶予を飛ばさないことを確認します。</summary>
public sealed class PeerDiscoveryGraceTests
{
    /// <summary>実heartbeatを進め、UTCの前進・後退と無関係に構成済みの発見時間を待ちます。</summary>
    /// <returns>ローカルUDPと保存済み復旧点を使った検証です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task TrustedRecoveryWaitsForElapsedDiscoveryGraceAsync(int correction)
    {
        using MediaTestFiles files = new();
        int port;
        using (UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        }
        string id = Guid.NewGuid().ToString("N");
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        ClusterOptions options = new(id, "one", "host", "127.0.0.1", "127.255.255.255", port, 1, false, files.Root)
        { PeerTimeout = TimeSpan.FromSeconds(10), HeartbeatInterval = TimeSpan.FromMilliseconds(25) };
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(30));
        await InitializeAsync(options, repository, watchdog.Token);
        RecoveryClock clock = new();
        using SemaphoreSlim observed = new(0);
        int count = 0;
        PeerCluster peer = new(options, repository, clock)
        {
            ReadinessCheck = token =>
            {
                Interlocked.Increment(ref count);
                observed.Release();
                return ValueTask.FromResult(true);
            },
        };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);

        // 構築からStartまでの待ち時間は、発見通知を受けられる期間に含めません。
        clock.Advance(TimeSpan.FromMinutes(1));
        await peer.StartAsync(watchdog.Token);
        clock.Adjust(TimeSpan.FromSeconds(correction));
        int before = Volatile.Read(ref count);
        while (Volatile.Read(ref count) < before + 2)
        {
            await observed.WaitAsync(watchdog.Token);
        }
        Assert.Equal(EPeerStatus.Syncing, Assert.Single(peer.GetPeers()).Status);
        clock.Advance(TimeSpan.FromSeconds(9));
        before = Volatile.Read(ref count);
        while (Volatile.Read(ref count) < before + 2)
        {
            await observed.WaitAsync(watchdog.Token);
        }
        Assert.Equal(EPeerStatus.Syncing, Assert.Single(peer.GetPeers()).Status);
        clock.Advance(TimeSpan.FromSeconds(1));
        while (Assert.Single(peer.GetPeers()).Status != EPeerStatus.Ready)
        {
            await observed.WaitAsync(watchdog.Token);
        }
        Assert.Equal("one", peer.CoordinatorId);
    }

    /// <summary>一度正常参加させ、既知集合と復旧点を製品の保存経路で作成します。</summary>
    /// <returns>試験用Coreの初回起動と停止です。</returns>
    private static async Task InitializeAsync(ClusterOptions options, SqliteRepository repository, CancellationToken cancellationToken = default)
    {
        PeerCluster initial = new(options with { NewCluster = true }, repository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = initial.ConfigureAwait(false);
        await initial.StartAsync(cancellationToken).ConfigureAwait(false);
        Assert.Equal(EPeerStatus.Ready, Assert.Single(initial.GetPeers()).Status);
    }

    /// <summary>実timerの通知を保ち、別スレッドから日時と経過時間を個別に進めます。</summary>
    private sealed class RecoveryClock : TimeProvider
    {
        private long elapsed;
        private long utc;

        /// <summary>経過tickの単位です。</summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>指定した経過時間です。</summary>
        /// <returns>開始からのtick数です。</returns>
        public override long GetTimestamp() => Interlocked.Read(ref elapsed);

        /// <summary>補正後の表示日時です。</summary>
        /// <returns>試験専用のUTCです。</returns>
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddYears(56).AddTicks(Interlocked.Read(ref utc));

        /// <summary>壁時計だけを補正します。</summary>
        internal void Adjust(TimeSpan amount) => Interlocked.Add(ref utc, amount.Ticks);

        /// <summary>発見猶予の消費を模擬します。</summary>
        internal void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref elapsed, amount.Ticks);
            Adjust(amount);
        }
    }
}
