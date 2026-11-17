using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>実通信の参加状態と、履歴整理だけの確認・保留を分けて検証します。</summary>
public sealed class HistoryRetentionPeerTests
{
    /// <summary>旧Peerや通知欠落では整理だけを止め、通常の現在値保存を拒否しません。</summary>
    /// <returns>対応照会と通常保存の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingClockOrUnsupportedPeerHoldsOnlyMaintenanceAsync(bool supported)
    {
        SyncRecoveryPeer peer = new(new([], []));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(30));
        await peer.StartAsync(watchdog.Token);
        await peer.EstablishAsync(watchdog.Token);
        peer.ReadCapabilities = () => new(HistoryRetention: supported);
        Assert.False(await peer.Target.Retention.ReadyAsync(watchdog.Token));
        Assert.Equal(supported ? "History.RetentionClockUnconfirmed"
                               : "History.RetentionPeerUpgradeRequired", peer.Target.Retention.Problem);
        Assert.Equal(EOutcome.Succeeded, (await peer.Repository.ApplyBatchAsync(Guid.CreateVersion7(),
            [new("workflow:retention-clock", "current", null, NodeValue.FromObject(7))], watchdog.Token)).Outcome);
        Assert.Equal(7, (await peer.Repository.ReadAsync("workflow:retention-clock", "current", watchdog.Token))!.Value!.Json.GetInt32());
    }

    /// <summary>通知の再送や未知の起動実体は、失効した確認を復活させません。</summary>
    /// <returns>受信境界の順序・実体・経過時間の確認です。</returns>
    [Fact]
    public async Task DuplicateAndUnknownInstanceCannotRefreshExpiredObservationAsync()
    {
        ElapsedClock time = new();
        SyncRecoveryPeer peer = new(new([], []), clock: time);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(30));
        await peer.StartAsync(watchdog.Token);
        await peer.EstablishAsync(watchdog.Token);
        peer.ReadCapabilities = () => new(HistoryRetention: true);
        PeerInfo source = peer.Target.GetPeers().Single(item => string.Equals(item.PeerId, "source", StringComparison.Ordinal));
        HistoryRetentionPeerGate retention = peer.Target.Retention;
        HistoryTimeReference reference = new(source.InstanceId, DateTimeOffset.UtcNow.AddDays(-31), 0);
        await retention.ReceiveAsync(source.PeerId, source.InstanceId, NodeValue.FromObject(new { Sequence = 10L, Reference = reference }), watchdog.Token);
        Assert.True(await retention.ReadyAsync(watchdog.Token));
        time.Advance(TimeSpan.FromSeconds(6));
        await peer.AnnounceAsync(await peer.Repository.GetSharedVersionAsync(watchdog.Token), cancellationToken: watchdog.Token);
        while (!peer.Target.GetPeers().Any(item => item.InstanceId == source.InstanceId && item.Status == EPeerStatus.Ready && source.LastSeen != item.LastSeen))
        {
            await Task.Delay(10, watchdog.Token);
        }
        await retention.ReceiveAsync(source.PeerId, source.InstanceId, NodeValue.FromObject(new { Sequence = 10L, Reference = reference }), watchdog.Token);
        Assert.False(await retention.ReadyAsync(watchdog.Token));
        await retention.ReceiveAsync(source.PeerId, Guid.NewGuid(), NodeValue.FromObject(new { Sequence = 11L, Reference = reference }), watchdog.Token);
        Assert.False(await retention.ReadyAsync(watchdog.Token));
        await retention.ReceiveAsync(source.PeerId, source.InstanceId, NodeValue.FromObject(new { Sequence = 9L, Reference = reference }), watchdog.Token);
        Assert.False(await retention.ReadyAsync(watchdog.Token));
        await retention.ReceiveAsync(source.PeerId, source.InstanceId, NodeValue.FromObject(new { Sequence = 11L, Reference = reference }), watchdog.Token);
        Assert.True(await retention.ReadyAsync(watchdog.Token));
    }

    /// <summary>小さい一時通知だけで古い基準を共有し、同期・通常保存も継続できます。</summary>
    /// <returns>実UDP/TCPの二つのPeer間での確認です。</returns>
    [Fact]
    public async Task RealPeerHeartbeatsShareTheReferenceWithoutBlockingWritesAsync()
    {
        using MediaTestFiles files = new();
        string cluster = Guid.NewGuid().ToString("N");
        int port = LargeSynchronizationTests.Port();
        SqliteRepository left = new(Path.Combine(files.Root, "left.db")), right = new(Path.Combine(files.Root, "right.db"));
        await using PeerCluster source = LargeSynchronizationTests.Create(left, files.Root, cluster, "source", port, true);
        await using PeerCluster target = LargeSynchronizationTests.Create(right, files.Root, cluster, "target", port, false);
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(40));
        HistoryTimeReference earlier = new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-31), 0);
        source.Retention.Clock.Observe(earlier);
        await source.StartAsync(watchdog.Token);
        await target.StartAsync(watchdog.Token);
        while (source.GetPeers().Count(item => item.Status == EPeerStatus.Ready) != 2 || target.GetPeers().Count(item => item.Status == EPeerStatus.Ready) != 2
            || !await source.Retention.ReadyAsync(watchdog.Token) || !await target.Retention.ReadyAsync(watchdog.Token)
            || target.Retention.Clock.Sample().OriginId != earlier.OriginId)
        {
            await Task.Delay(20, watchdog.Token);
        }
        Assert.True(target.Retention.Clock.Capture() < DateTimeOffset.UtcNow.AddDays(-30));
        Guid operation = Guid.CreateVersion7();
        Assert.Equal(EOutcome.Succeeded, (await target.ReplicateAsync(operation,
            [new("workflow:retention-clock", "current", null, NodeValue.FromObject(9))], watchdog.Token)).Outcome);
        Assert.Equal(9, (await left.ReadAsync("workflow:retention-clock", "current", watchdog.Token))!.Value!.Json.GetInt32());
    }

    /// <summary>UTCと通信の実時間は変えず、通知の生存期間だけを経過させます。</summary>
    private sealed class ElapsedClock : TimeProvider
    {
        private long offset;

        /// <summary>プロセスの単調時計へ、試験が指定した経過分を加えます。</summary>
        /// <returns>生存期間の判定に使うカウンターです。</returns>
        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref offset);

        /// <summary>既定の通知期限を経過させ、実際の秒待ちを省きます。</summary>
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref offset, checked((long)(elapsed.TotalSeconds * TimestampFrequency)));
    }
}
