using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>概要への転送量配信・単位・自動目盛りと、欠測を検証します。</summary>
public sealed class DashboardThroughputTests
{
    /// <summary>選択NICに依存しない合計値を配信し、個別NICの累積値とは混同しません。</summary>
    [Fact]
    public void SummaryIncludesHostRatesWithoutExtraNicDetails()
    {
        using ClientCultureScope culture = new("en-US");
        ResourceSample sample = Sample(1048576) with { Metrics = [
            new("disk.read.rate", "machine", 1048576, "bytes/s"), new("disk.write.rate", "machine", 2097152, "bytes/s"),
            new("network.sent.rate", "machine", 12500000, "bytes/s"), new("network.received.rate", "machine", 25000000, "bytes/s"),
            new("network.sent", "unselected", 987654321, "bytes"), new("disk.total", "C:", 987654321, "bytes"),
            new("disk.read.rate", "individual", 2097152, "bytes/s"),
        ] };
        ResourceSample projected = DashboardSampleHistory.Project(sample);
        Assert.Equal(4, projected.Metrics.Count);
        Assert.All(projected.Metrics, metric => Assert.Equal("machine", metric.Target));
        Assert.Equal("1.00", OverviewHostCard.Throughput(DashboardMetrics.Value(projected, "disk.read.rate"), 1048576));
        Assert.Equal("100.00", OverviewHostCard.Throughput(DashboardMetrics.Value(projected, "network.sent.rate"), 125000));
        Assert.Equal("200.00", OverviewHostCard.Throughput(DashboardMetrics.Value(projected, "network.received.rate"), 125000));
        Assert.Equal("—", OverviewHostCard.Throughput(null, 125000));
        Assert.Equal("—", OverviewHostCard.Throughput(-1, 125000));
        Assert.Equal("0.00", OverviewHostCard.Throughput(0, 125000));
    }

    /// <summary>100を超える速度も割合扱いせず、見えている実体の最大値から目盛りを作ります。</summary>
    [Fact]
    public void RatesUseTheirOwnScaleAndDoNotBridgeMissingSamples()
    {
        ResourceSample first = Sample(1048576);
        ResourceSample missing = first with { Id = Guid.NewGuid(), ObservedAt = first.ObservedAt.AddSeconds(3), Metrics = [] };
        ResourceSample last = first with { Id = Guid.NewGuid(), ObservedAt = first.ObservedAt.AddSeconds(6), Metrics = [new("disk.read.rate", "machine", 2.5 * 1048576, "bytes/s")] };
        ResourceSample old = first with { ObservedAt = first.ObservedAt.AddMinutes(-7), Metrics = [new("disk.read.rate", "machine", 999999999, "bytes/s")] };
        ResourceSample other = last with { InstanceId = Guid.NewGuid(), Metrics = old.Metrics };
        double maximum = DashboardMetrics.ThroughputMaximum([old, first, other, missing, last], "disk.read.rate", 1048576);
        Assert.Equal(3 * 1048576, maximum);
        Assert.Contains("L300.000,16.67", DashboardMetrics.Trend([first, last], "disk.read.rate", "machine", maximum), StringComparison.Ordinal);
        Assert.DoesNotContain("L", DashboardMetrics.Trend([first, missing, last], "disk.read.rate", "machine", maximum), StringComparison.Ordinal);
        Assert.Equal(125000, DashboardMetrics.ThroughputMaximum([], "network.sent.rate", 125000));
        Assert.Equal(string.Empty, DashboardMetrics.Trend([first, last], "disk.read.rate", "machine", 0));
    }

    /// <summary>比較に使う一つの実測を作ります。</summary>
    /// <returns>同じホスト実体の転送量サンプルです。</returns>
    private static ResourceSample Sample(double value) => new(Guid.NewGuid(), "peer", DateTimeOffset.UtcNow,
        [new("disk.read.rate", "machine", value, "bytes/s")], InstanceId: Guid.NewGuid());
}
