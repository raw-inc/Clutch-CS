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

    /// <summary>採取と応答の間、および取得と選択の間の日時補正を鮮度へ持ち込みません。</summary>
    /// <returns>実通信で取得した経過時間と、同じ標本の再取得時の期限です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task SourceAndReceiverClockStepsDoNotChangeTheSameSamplesAgeAsync(int correction)
    {
        ResourceSelectionClock receiverClock = new(), sourceClock = new();
        string cluster = Guid.NewGuid().ToString("N");
        int port = Port();
        PeerCluster receiver = Peer(cluster, "receiver", port, true), source = Peer(cluster, "source", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable receiverLifetime = receiver.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = source.ConfigureAwait(true);
        ResourceSample sample = Sample(source, sourceClock.GetUtcNow()) with { Age = new Contracts.Observability.ObservationAge(sourceClock) };
        sourceClock.Advance(TimeSpan.FromSeconds(3));
        sourceClock.Adjust(TimeSpan.FromSeconds(correction));
        ResourceSelectionExchange incoming = new(receiver, () => null, receiverClock), outgoing = new(source, () => sample, sourceClock);
        Assert.NotNull(outgoing);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await receiver.StartAsync(timeout.Token).ConfigureAwait(true);
        await source.StartAsync(timeout.Token).ConfigureAwait(true);
        await ReadyAsync(receiver, source, timeout.Token).ConfigureAwait(true);
        await incoming.RefreshAsync(timeout.Token).ConfigureAwait(true);
        ResourceSample captured = incoming.Capture()[source.LocalPeerId];
        Assert.Equal(TimeSpan.FromSeconds(3), captured.Age!.Value.Elapsed);
        receiverClock.Adjust(TimeSpan.FromSeconds(-correction));
        Assert.Null(Compare(source, captured, receiverClock).Reason);
        ResourceSample fixedPlan = captured with { Age = captured.Age.Value.Freeze() };
        receiverClock.Advance(TimeSpan.FromSeconds(8));
        sourceClock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal("selection.observationExpired", Compare(source, captured, receiverClock).Reason);
        Assert.Null(Compare(source, fixedPlan, receiverClock).Reason);
        await incoming.RefreshAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal("selection.observationExpired", Compare(source, incoming.Capture()[source.LocalPeerId], receiverClock).Reason);
        Assert.Equal(TimeSpan.FromSeconds(11), sample.Age!.Value.Elapsed);
    }

    /// <summary>新しい照会方式への対応を確認するまでは、先に届いた旧形式の通知を比較に使わないことを確認します。</summary>
    /// <returns>対応確認前の通知が採用されないことを検証するタスクです。</returns>
    [Fact]
    public async Task UnconfirmedCapabilityDoesNotTrustLegacyTimestamps()
    {
        await using PeerCluster peer = Peer(Guid.NewGuid().ToString("N"), "peer", Port(), true);
        ResourceSelectionExchange incoming = new(peer, () => null);
        await peer.StartAsync();

        // PublishTransientAsyncは自分の購読の完了を待つため、UDP到着順や待ち時間には依存しません。
        await peer.PublishTransientAsync("resources.selection", NodeValue.FromObject(Sample(peer, DateTimeOffset.UtcNow)));
        Assert.Empty(incoming.Capture());
    }

    /// <summary>PC間の時計差や時刻補正があっても、採取から10秒を超えた標本が比較対象から外れることを確認します。</summary>
    /// <returns>鮮度の判定と再取得時の扱いを検証するタスクです。</returns>
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
        ResourceSample sample = Sample(source, sourceClock.GetUtcNow().AddSeconds(-3)) with
        { Age = new Contracts.Observability.ObservationAge(sourceClock, TimeSpan.FromSeconds(3)) };
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
        sample = Sample(source, sourceClock.GetUtcNow()) with { Age = new Contracts.Observability.ObservationAge(sourceClock) };
        await incoming.RefreshAsync(timeout.Token);
        Assert.Equal(sample.Id, incoming.Capture()[source.LocalPeerId].Id);
        Assert.Null(Compare(source, incoming.Capture()[source.LocalPeerId], receiverClock).Reason);
        Assert.Empty(await new SqliteRepository(Path.Combine(files.Root, "source.db")).ListAsync("receipts:source", timeout.Token));
    }

    /// <summary>通信待ちも標本の古さに加算し、採取時刻が不正な標本や別のCoreプロセスの標本を除外することを確認します。</summary>
    /// <returns>応答遅延と不正な標本の扱いを検証するタスクです。</returns>
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

    /// <summary>旧Coreからの通知は、PC間の時計が合っていれば従来どおり比較に使えることを確認します。</summary>
    /// <returns>未対応の応答を受けてから旧形式の通知を採用することを検証するタスクです。</returns>
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

    /// <summary>両Peerが互いをReadyと認識するまで待ちます。</summary>
    /// <returns>両Peerの参加確認を表すタスクです。</returns>
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
