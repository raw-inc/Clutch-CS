using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>実際のPeer間通信で、低負荷PC選択の鮮度と時計差を検証します。</summary>
public sealed class ResourceSelectionClockTests : IDisposable
{
    private readonly MediaTestFiles files = new();

    /// <summary>PC間の時計差・再照会・時計補正が、十秒の採取鮮度を変えません。</summary>
    /// <returns>新しい標本だけを選び、収集停止後は選ばないことです。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task SelectionUsesElapsedAgeInsteadOfAnotherPcClock(int offsetSeconds)
    {
        ResourceSelectionClock receiverClock = new(), sourceClock = new();
        sourceClock.Adjust(TimeSpan.FromSeconds(offsetSeconds));
        string cluster = Guid.NewGuid().ToString("N");
        int port = Port();
        await using PeerCluster receiver = Peer(cluster, "receiver", port, true);
        await using PeerCluster source = Peer(cluster, "source", port, false);
        ResourceSample sample = Sample(source, sourceClock.GetUtcNow().AddSeconds(-3));
        ResourceSelectionExchange incoming = new(receiver, () => null, receiverClock);
        ResourceSelectionExchange outgoing = new(source, () => sample, sourceClock);
        Assert.NotNull(outgoing);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await receiver.StartAsync(timeout.Token);
        await source.StartAsync(timeout.Token);
        await ReadyAsync(receiver, source, timeout.Token);
        await incoming.RefreshAsync(timeout.Token);
        ResourceSample first = incoming.Capture()[source.LocalPeerId];
        Assert.Equal(receiverClock.GetUtcNow().AddSeconds(-3), first.ObservedAt);
        Assert.Null(Compare(source, first, receiverClock).Reason);
        Assert.Equal(sourceClock.GetUtcNow().AddSeconds(-3), sample.ObservedAt);

        receiverClock.Advance(TimeSpan.FromSeconds(7));
        sourceClock.Advance(TimeSpan.FromSeconds(7));
        receiverClock.Adjust(TimeSpan.FromHours(-2));
        await incoming.RefreshAsync(timeout.Token);
        ResourceSample repeated = incoming.Capture()[source.LocalPeerId];
        Assert.Equal(TimeSpan.FromSeconds(10), receiverClock.GetUtcNow() - repeated.ObservedAt);
        Assert.Null(Compare(source, repeated, receiverClock).Reason);
        receiverClock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("selection.observationExpired", Compare(source, incoming.Capture()[source.LocalPeerId], receiverClock).Reason);

        // 採取元の時計が戻っても、新しい標本IDなら古い日時順で捨てません。
        receiverClock.Advance(TimeSpan.FromSeconds(3));
        sourceClock.Adjust(TimeSpan.FromHours(-3));
        sample = Sample(source, sourceClock.GetUtcNow());
        await incoming.RefreshAsync(timeout.Token);
        Assert.Equal(sample.Id, incoming.Capture()[source.LocalPeerId].Id);
        Assert.Null(Compare(source, incoming.Capture()[source.LocalPeerId], receiverClock).Reason);
        Assert.Empty(await new SqliteRepository(Path.Combine(files.Root, "source.db")).ListAsync("receipts:source", timeout.Token));
    }

    /// <summary>応答遅延を鮮度から除外せず、未来や別実体の標本も採用しません。</summary>
    /// <returns>遅い応答・不正な標本を低負荷の候補にしないことです。</returns>
    [Theory]
    [InlineData("delay")]
    [InlineData("future")]
    [InlineData("instance")]
    public async Task UnconfirmedSampleCannotBecomeASelectableLowLoadPc(string problem)
    {
        ResourceSelectionClock clock = new();
        string cluster = Guid.NewGuid().ToString("N");
        int port = Port();
        await using PeerCluster receiver = Peer(cluster, "receiver", port, true);
        await using PeerCluster source = Peer(cluster, "source", port, false);
        ResourceSelectionExchange incoming = new(receiver, () => null, clock);
        source.RegisterInspection("resources.selection.current", (_, _) =>
        {
            DateTimeOffset sourceNow = clock.GetUtcNow().AddHours(1);
            ResourceSample sample = Sample(source, sourceNow.AddSeconds(-9));
            switch (problem)
            {
                case "delay": clock.Advance(TimeSpan.FromSeconds(2)); break;
                case "future": sample = sample with { ObservedAt = sourceNow.AddSeconds(1) }; break;
                case "instance": sample = sample with { InstanceId = Guid.NewGuid() }; break;
            }
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded,
                NodeValue.FromObject(new ResourceSelectionSnapshot(sample, sourceNow))));
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await receiver.StartAsync(timeout.Token);
        await source.StartAsync(timeout.Token);
        await ReadyAsync(receiver, source, timeout.Token);
        await incoming.RefreshAsync(timeout.Token);
        incoming.Capture().TryGetValue(source.LocalPeerId, out ResourceSample? received);
        Assert.NotNull(Compare(source, received, clock).Reason);
    }

    /// <summary>日時が一致する旧Coreの通知は、従来どおり有限の鮮度で利用します。</summary>
    /// <returns>未対応の照会を確認した後の互換経路です。</returns>
    [Fact]
    public async Task LegacyNotificationsRemainAvailableForOlderPeers()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = Port();
        await using PeerCluster receiver = Peer(cluster, "receiver", port, true);
        await using PeerCluster source = Peer(cluster, "source", port, false);
        ResourceSelectionExchange incoming = new(receiver, () => null);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await receiver.StartAsync(timeout.Token);
        await source.StartAsync(timeout.Token);
        await ReadyAsync(receiver, source, timeout.Token);
        await incoming.RefreshAsync(timeout.Token);
        ResourceSample sample = Sample(source, DateTimeOffset.UtcNow);
        await source.PublishTransientAsync("resources.selection", NodeValue.FromObject(sample), timeout.Token);
        while (!incoming.Capture().ContainsKey(source.LocalPeerId))
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal(sample.ObservedAt, incoming.Capture()[source.LocalPeerId].ObservedAt);
        Assert.Null(Compare(source, incoming.Capture()[source.LocalPeerId], TimeProvider.System).Reason);
    }

    /// <summary>選択時と同じ鮮度・実体・単位の検証を通します。</summary>
    /// <returns>CPU比較の可否です。</returns>
    private static PcSelectionObservation Compare(PeerCluster source, ResourceSample? sample, TimeProvider clock)
        => PcResourceComparison.Read(source.GetPeers().Single(peer => peer.InstanceId == source.LocalInstanceId), sample, new(EPcResource.Cpu), clock.GetUtcNow());

    /// <summary>負荷が低い測定値を作ります。</summary>
    /// <returns>CPU比較に必要な標本です。</returns>
    private static ResourceSample Sample(PeerCluster source, DateTimeOffset observedAt)
        => new(Guid.NewGuid(), source.LocalPeerId, observedAt, [new("cpu.usage", "host", 1, "percent")], InstanceId: source.LocalInstanceId);

    /// <summary>通常のPeer時計は変更せず、計測値の時計だけを試験で差し替えます。</summary>
    /// <returns>隔離DBとループバック通信を持つPeerです。</returns>
    private PeerCluster Peer(string cluster, string peer, int port, bool initialize)
    {
        ClusterOptions options = new(cluster, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, initialize, Path.Combine(files.Root, peer))
        { HeartbeatInterval = TimeSpan.FromMilliseconds(50), PeerTimeout = TimeSpan.FromSeconds(2), RequestTimeout = TimeSpan.FromSeconds(2) };
        return new(options, new SqliteRepository(Path.Combine(files.Root, peer + ".db"))) { ReadinessCheck = _ => ValueTask.FromResult(true) };
    }

    /// <summary>相互の参加確認後に資源比較を開始します。</summary>
    /// <returns>通常の初回参加の完了です。</returns>
    private static async Task ReadyAsync(PeerCluster first, PeerCluster second, CancellationToken cancellationToken = default)
    {
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2 || second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>試験用のUDPポート番号を確保します。</summary>
    /// <returns>空いているポート番号です。</returns>
    private static int Port()
    {
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>Peer停止後に試験専用DBを削除します。</summary>
    public void Dispose() => files.Dispose();
}
