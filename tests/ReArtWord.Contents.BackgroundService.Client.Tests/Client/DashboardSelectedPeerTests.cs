using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>接続中Core自身の古い生存通知時刻で、確認済みの実測系列を欠測にしません。</summary>
public sealed class DashboardSelectedPeerTests
{
    /// <summary>同じ実体のClientHost観測で生存時刻を更新し、別実体なら採用しません。</summary>
    /// <returns>PCカードへ渡す最新の実体と、折れ線の二点の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedPeerUsesTheMatchingObservedHeartbeatAsync(bool differentInstance)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid instance = Guid.NewGuid();
        PeerInfo statusPeer = new("peer", instance, "host", "127.0.0.1", 47740, EPeerStatus.Ready, 1, now.AddHours(-1), now.AddHours(-1));
        PeerInfo observed = statusPeer with { LastSeen = now };
        if (differentInstance)
        {
            observed = observed with { InstanceId = Guid.NewGuid() };
        }
        ResourceSample first = new(Guid.NewGuid(), "peer", now.AddSeconds(-3), [new("cpu.usage", "machine", 25, "percent")], InstanceId: observed.InstanceId);
        ResourceSample second = first with { Id = Guid.NewGuid(), ObservedAt = now };
        DashboardPeerSnapshot snapshot = new("cluster", observed, now, null,
            JsonSerializer.SerializeToElement(new PeerHealth("cluster", observed.PeerId, observed.InstanceId, EPeerStatus.Ready, null), ContractJson.Options), [first, second]);
        using HttpClient http = new() { BaseAddress = new Uri("http://localhost") };
        ManagementApi api = new(http) { SelectedClusterId = "cluster", SelectedPeerId = "peer" };
        DashboardTelemetry telemetry = new(api);
        await telemetry.RefreshTargetAsync("cluster", "peer", instance, [statusPeer], true, [snapshot]).ConfigureAwait(true);
        OverviewPeer peer = Assert.Single(telemetry.Peers);
        if (differentInstance)
        {
            Assert.Empty(peer.Samples);
            Assert.NotNull(peer.Problem);
            return;
        }
        Assert.Equal(now, peer.Peer.LastSeen);
        Assert.Equal(statusPeer.ReadySince, peer.Peer.ReadySince);
        Assert.Equal(2, peer.Samples.Count);
        Assert.Null(peer.Problem);
        Assert.Contains("L", DashboardMetrics.Trend(peer.Samples, "cpu.usage", "machine"), StringComparison.Ordinal);
    }
}
