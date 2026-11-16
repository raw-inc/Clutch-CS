using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Tests.Client;
using ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>PC間の時計差と、同じ標本の再取得・収集停止を区別します。</summary>
public sealed class DashboardClockTests
{
    /// <summary>送信元の時計が前後していても、実際の採取経過時間で鮮度を判定します。</summary>
    /// <returns>履歴・軽量照会・圧縮配信に共通の時刻基準です。</returns>
    [Theory]
    [InlineData(-3600)]
    [InlineData(-20)]
    [InlineData(20)]
    [InlineData(3600)]
    public async Task FreshSamplesUseTheirSourceAgeDespiteClockOffsets(int offsetSeconds)
    {
        EditorTestClock clock = new();
        PeerInfo peer = Peer(clock.GetUtcNow());
        DateTimeOffset sourceNow = clock.GetUtcNow().AddSeconds(offsetSeconds);
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, sourceNow.AddSeconds(-3), [], InstanceId: peer.InstanceId);
        using DashboardCache cache = new(() => [new("cluster", [peer], [], clock.GetUtcNow())],
            (_, _) => Task.FromResult<JsonElement?>(Bundle(peer, sample, sourceNow)), clock);
        await cache.RefreshAsync("cluster", peer, false);
        DashboardPeerSnapshot first = cache.Read("cluster", peer.PeerId)!;
        ResourceSample received = Assert.Single(first.Samples);
        Assert.Equal(clock.GetUtcNow().AddSeconds(-3), received.ObservedAt);
        Assert.Equal(sourceNow.AddSeconds(-3), sample.ObservedAt);
        Assert.False(DashboardMetrics.IsStale(received, clock.GetUtcNow()));
        Assert.NotNull(first.Health);
        Assert.Null(first.Problem);

        clock.Advance(TimeSpan.FromSeconds(3));
        sourceNow = sourceNow.AddSeconds(3);
        sample = sample with { Id = Guid.NewGuid(), ObservedAt = sample.ObservedAt.AddSeconds(3) };
        await cache.RefreshAsync("cluster", peer, false);
        ResourceSample latest = Assert.Single(cache.Read("cluster", peer.PeerId, received.Id)!.Samples);
        Assert.Equal(TimeSpan.FromSeconds(3), latest.ObservedAt - received.ObservedAt);
        Assert.Equal(2, cache.Read("cluster", peer.PeerId)!.Samples.Count);
        Assert.Empty(cache.Read("cluster", peer.PeerId, healthOnly: true)!.Samples);
    }

    /// <summary>healthの再確認だけでは、収集が止まった標本の時刻を更新しません。</summary>
    /// <returns>既存の15秒の鮮度境界と履歴の保持期限です。</returns>
    [Fact]
    public async Task RepeatedRepliesDoNotRefreshAFrozenSample()
    {
        EditorTestClock clock = new();
        PeerInfo peer = Peer(clock.GetUtcNow());
        DateTimeOffset sourceNow = clock.GetUtcNow().AddMinutes(-5);
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, sourceNow.AddSeconds(-3), [], InstanceId: peer.InstanceId);
        using DashboardCache cache = new(() => [new("cluster", [peer], [], clock.GetUtcNow())],
            (_, _) => Task.FromResult<JsonElement?>(Bundle(peer, sample, sourceNow)), clock);
        await cache.RefreshAsync("cluster", peer, false);
        ResourceSample first = Assert.Single(cache.Read("cluster", peer.PeerId)!.Samples);
        clock.Advance(TimeSpan.FromSeconds(13));
        sourceNow = sourceNow.AddSeconds(13);
        await cache.RefreshAsync("cluster", peer, false);
        DashboardPeerSnapshot current = cache.Read("cluster", peer.PeerId)!;
        Assert.Equal(first.ObservedAt, Assert.Single(current.Samples).ObservedAt);
        Assert.NotNull(current.Health);
        Assert.True(DashboardMetrics.IsStale(current.Samples[0], clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(7));
        sourceNow = sourceNow.AddMinutes(7);
        await cache.RefreshAsync("cluster", peer, false);
        Assert.Empty(cache.Read("cluster", peer.PeerId)!.Samples);
    }

    /// <summary>遅れて届いた応答を、その受信時刻で新しく見せません。</summary>
    /// <returns>通信待ち時間を含めた経過時間です。</returns>
    [Fact]
    public async Task TransportDelayRemainsPartOfSampleAge()
    {
        EditorTestClock clock = new();
        PeerInfo peer = Peer(clock.GetUtcNow());
        DateTimeOffset sourceNow = clock.GetUtcNow().AddMinutes(1);
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, sourceNow.AddSeconds(-14), [], InstanceId: peer.InstanceId);
        using DashboardCache cache = new(() => [new("cluster", [peer], [], clock.GetUtcNow())], (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult<JsonElement?>(Bundle(peer, sample, sourceNow));
        }, clock);
        await cache.RefreshAsync("cluster", peer, false);
        ResourceSample received = Assert.Single(cache.Read("cluster", peer.PeerId)!.Samples);
        Assert.Equal(TimeSpan.FromSeconds(16), clock.GetUtcNow() - received.ObservedAt);
        Assert.True(DashboardMetrics.IsStale(received, clock.GetUtcNow()));
    }

    /// <summary>壊れた応答時刻や、送信元でも未来の標本は稼働確認から分離します。</summary>
    /// <returns>不正な計測だけを採用しないことです。</returns>
    [Theory]
    [InlineData("invalid")]
    [InlineData(null)]
    [InlineData("2020-01-01T00:00:00Z")]
    public async Task InvalidSourceTimesDoNotInvalidateConfirmedHealth(string? timestamp)
    {
        EditorTestClock clock = new();
        PeerInfo peer = Peer(clock.GetUtcNow());
        ResourceSample sample = new(Guid.NewGuid(), peer.PeerId, clock.GetUtcNow(), [], InstanceId: peer.InstanceId);
        using DashboardCache cache = new(() => [new("cluster", [peer], [], clock.GetUtcNow())],
            (_, _) => Task.FromResult<JsonElement?>(Bundle(peer, sample, timestamp)), clock);
        await cache.RefreshAsync("cluster", peer, false);
        DashboardPeerSnapshot result = cache.Read("cluster", peer.PeerId)!;
        Assert.NotNull(result.Health);
        Assert.NotNull(result.Problem);
        Assert.Empty(result.Samples);
    }

    /// <summary>実体が一致する応答を作ります。</summary>
    /// <returns>Coreの一括照会と同じ形の本文です。</returns>
    private static JsonElement Bundle(PeerInfo peer, ResourceSample sample, object? sourceNow)
    {
        var health = new { clusterId = "cluster", peerId = peer.PeerId, instanceId = peer.InstanceId, status = EPeerStatus.Ready };
        return JsonSerializer.SerializeToElement(new { health, status = health, resource = sample, observedAt = sourceNow }, ContractJson.Options);
    }

    /// <summary>試験で観測するPeerを作ります。</summary>
    /// <returns>同じ実体を指す通知です。</returns>
    private static PeerInfo Peer(DateTimeOffset now) => new("peer", Guid.NewGuid(), "host", "127.0.0.1", 47740, EPeerStatus.Ready, 0, now, now);
}
