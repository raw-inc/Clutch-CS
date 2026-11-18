using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ManagementTransport;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>両実体の単調時計へ固定した期限が、日時補正・再照会・逆方向転送で延長されないことを確認します。</summary>
public sealed class SyncSessionClockTests : IAsyncDisposable
{
    private readonly MediaTestFiles files = new();
    private readonly AdmissionTestClock sourceClock = new(), targetClock = new();
    private readonly PeerCluster source;
    private readonly PeerCluster target;
    private readonly SyncSessionClocks sourceSessions;
    private readonly SyncSessionClocks targetSessions;

    /// <summary>OS時計や実稼働Peerを使わない、別実体の時計を作ります。</summary>
    public SyncSessionClockTests()
    {
        string cluster = Guid.NewGuid().ToString("N");
        source = Create("source", cluster, sourceClock);
        target = Create("target", cluster, targetClock);
        sourceSessions = new(source, sourceClock);
        targetSessions = new(target, targetClock);
    }

    /// <summary>通信遅延を期限へ足し戻さず、UTCを補正しても同じ期限を両方向で消費します。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void FixedBidirectionalDeadlinesSurviveClockCorrectionsAndDoNotRenew(int correction)
    {
        RequestClockSnapshot sampled = target.AdmissionClock.Capture();
        long started = sourceClock.GetTimestamp();
        sourceClock.Advance(TimeSpan.FromSeconds(2));
        targetClock.Advance(TimeSpan.FromSeconds(2));
        sourceSessions.Observe(target.LocalInstanceId, sampled, started);
        SyncLifetime owned = new(sourceClock, TimeSpan.FromSeconds(10));
        SyncTiming timing = Assert.IsType<SyncTiming>(sourceSessions.Create(target.LocalInstanceId, owned));
        sourceClock.Adjust(TimeSpan.FromSeconds(correction));
        targetClock.Adjust(TimeSpan.FromSeconds(-correction));
        SyncLifetime accepted = targetSessions.Accept(timing, sourceClock.GetUtcNow().AddSeconds(10), source.LocalInstanceId, TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(8), accepted.Remaining);
        sourceClock.Advance(TimeSpan.FromSeconds(3));
        targetClock.Advance(TimeSpan.FromSeconds(3));
        targetClock.Adjust(TimeSpan.FromSeconds(correction * 2));
        SyncLifetime repeated = targetSessions.Accept(timing, sourceClock.GetUtcNow().AddSeconds(7), source.LocalInstanceId, TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(5), accepted.Remaining);
        Assert.Equal(accepted.Remaining, repeated.Remaining);
        SyncTiming reverse = Assert.IsType<SyncTiming>(targetSessions.Reverse(timing, source.LocalInstanceId, accepted));
        SyncLifetime returning = sourceSessions.Accept(reverse, targetClock.GetUtcNow().AddSeconds(5), target.LocalInstanceId, TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(5), returning.Remaining);
        Assert.True(returning.Remaining <= owned.Remaining);
        sourceClock.Advance(TimeSpan.FromSeconds(5));
        targetClock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(accepted.Expired);
        Assert.True(returning.Expired);
        Assert.Equal("Sync.SessionExpired", Assert.Throws<SyncFailureException>(() => targetSessions.Accept(timing, DateTimeOffset.MaxValue, source.LocalInstanceId, TimeSpan.FromHours(1))).Code);
        Assert.Equal("Sync.SessionExpired", Assert.Throws<SyncFailureException>(() => sourceSessions.Accept(reverse, DateTimeOffset.MaxValue, target.LocalInstanceId, TimeSpan.FromHours(1))).Code);
    }

    /// <summary>再起動後の別実体や不正な有効期間を、同じ同期の期限として採用しません。</summary>
    [Fact]
    public void InstanceAndDurationChecksRejectChangedClockContracts()
    {
        sourceSessions.Observe(target.LocalInstanceId, target.AdmissionClock.Capture(), sourceClock.GetTimestamp());
        SyncTiming timing = Assert.IsType<SyncTiming>(sourceSessions.Create(target.LocalInstanceId, new(sourceClock, TimeSpan.FromSeconds(10))));
        foreach (SyncTiming invalid in new[]
        {
            timing with { SourceInstanceId = Guid.NewGuid() },
            timing with { TargetInstanceId = Guid.NewGuid() },
            timing with { MaximumLifetimeTicks = 0 },
            timing with { MaximumLifetimeTicks = TimeSpan.FromHours(2).Ticks },
            timing with { TargetUntilTicks = TimeSpan.FromHours(2).Ticks },
            timing with { ClockUncertaintyTicks = -1 },
        })
        {
            Assert.Equal("Sync.ManifestMismatch", Assert.Throws<SyncFailureException>(() => targetSessions.Accept(invalid, DateTimeOffset.MaxValue, source.LocalInstanceId, TimeSpan.FromHours(1))).Code);
        }
        Assert.Throws<SyncFailureException>(() => sourceSessions.Observe(target.LocalInstanceId, new(Guid.NewGuid(), 0), sourceClock.GetTimestamp()));
        sourceSessions.Observe(target.LocalInstanceId, new(target.LocalInstanceId, long.MaxValue), sourceClock.GetTimestamp());
        Assert.Equal("Sync.ClockUnconfirmed", Assert.Throws<SyncFailureException>(() => sourceSessions.Create(target.LocalInstanceId, new(sourceClock, TimeSpan.FromSeconds(10)))).Code);
    }

    /// <summary>未対応との応答を確認した旧形式も、受入後は同じ残り時間を使います。</summary>
    [Fact]
    public void LegacyAdmissionKeepsItsOriginalLifetimeAfterClockCorrection()
    {
        sourceSessions.Observe(target.LocalInstanceId, null, sourceClock.GetTimestamp());
        Assert.Null(sourceSessions.Create(target.LocalInstanceId, new(sourceClock, TimeSpan.FromSeconds(10))));
        SyncLifetime accepted = targetSessions.Accept(null, targetClock.GetUtcNow().AddSeconds(10), source.LocalInstanceId, TimeSpan.FromHours(1));
        targetClock.Adjust(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromSeconds(10), accepted.Remaining);
        targetClock.Advance(TimeSpan.FromSeconds(4));
        SyncLifetime shortened = accepted.Limit(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), shortened.Remaining);
        Assert.Same(accepted, accepted.Limit(TimeSpan.FromSeconds(8)));
        targetClock.Adjust(TimeSpan.FromHours(-2));
        targetClock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(accepted.Expired);
        Assert.True(shortened.Expired);
    }

    /// <summary>時計を照会する実体だけを作り、通信は開始しません。</summary>
    /// <returns>試験が所有するPeerです。</returns>
    private PeerCluster Create(string peer, string cluster, TimeProvider clock) => new(new ClusterOptions(cluster, peer, peer, "127.0.0.1", "127.255.255.255", 12345, 1, false, files.Root), new SqliteRepository(Path.Combine(files.Root, peer + ".db")), clock);

    /// <summary>構築したPeerと試験用保存先を解放します。</summary>
    /// <returns>所有資源の解放です。</returns>
    public async ValueTask DisposeAsync()
    {
        await ((IAsyncDisposable)source).DisposeAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)target).DisposeAsync().ConfigureAwait(false);
        files.Dispose();
    }
}
