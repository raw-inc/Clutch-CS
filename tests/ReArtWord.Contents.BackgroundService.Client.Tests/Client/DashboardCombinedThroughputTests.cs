using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>双方向の合計を数値・グラフ・目盛りで共有し、欠測や単位を維持することを検証します。</summary>
public sealed class DashboardCombinedThroughputTests
{
    /// <summary>IOの合計値をそのままグラフと上限へ使い、方向別の値も保持します。</summary>
    [Fact]
    public void DiskTotalDrivesNumberTrendAndScale()
    {
        ResourceSample first = Sample(1048576, 3 * 1048576);
        ResourceSample last = first with { Id = Guid.NewGuid(), ObservedAt = first.ObservedAt.AddSeconds(3), Metrics = Sample(2 * 1048576, 1048576d).Metrics };
        Assert.Equal(3 * 1048576, DashboardMetrics.Value(last, "disk.transfer.rate"));
        Assert.Equal(2 * 1048576, DashboardMetrics.Value(last, "disk.read.rate"));
        Assert.Equal(1048576, DashboardMetrics.Value(last, "disk.write.rate"));
        double maximum = DashboardMetrics.ThroughputMaximum([first, last], "disk.transfer.rate", 1048576d);
        Assert.Equal(4 * 1048576, maximum);
        Assert.Equal("M297.000,0.00 L300.000,25.00 ", DashboardMetrics.Trend([first, last], "disk.transfer.rate", "machine", maximum));
    }

    /// <summary>NWの合計も同一サンプルのbytes/sから作り、Kbps/Mbpsの表示選択はグラフ値を変えません。</summary>
    [Fact]
    public void NetworkTotalUsesBothDirectionsWithStableByteScale()
    {
        ResourceSample first = Sample(0, 0d) with { Metrics = [new("network.received.rate", "machine", 50000d, "bytes/s"), new("network.sent.rate", "machine", 100000d, "bytes/s")] };
        ResourceSample last = first with { Id = Guid.NewGuid(), ObservedAt = first.ObservedAt.AddSeconds(3), Metrics = [new("network.received.rate", "machine", 90000d, "bytes/s"), new("network.sent.rate", "machine", 210000d, "bytes/s")] };
        Assert.Equal(150000, DashboardMetrics.Value(first, "network.transfer.rate"));
        Assert.Equal(("1.20", "Mbps"), OverviewHostCard.NetworkThroughput(DashboardMetrics.Value(first, "network.transfer.rate")));
        double maximum = DashboardMetrics.ThroughputMaximum([first, last], "network.transfer.rate", 125d);
        Assert.Equal(375000, maximum);
        Assert.Equal("M297.000,60.00 L300.000,20.00 ", DashboardMetrics.Trend([first, last], "network.transfer.rate", "machine", maximum));
    }

    /// <summary>片側欠測・負値・非有限値や加算時のオーバーフローを部分合計で代替しません。</summary>
    [Theory]
    [InlineData(null, 1d)]
    [InlineData(1d, null)]
    [InlineData(-1d, 2d)]
    [InlineData(2d, -1d)]
    [InlineData(double.NaN, 2d)]
    [InlineData(2d, double.PositiveInfinity)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public void IncompleteTotalsStayUnknown(double? read, double? write)
    {
        Assert.Null(DashboardMetrics.Value(Sample(read, write), "disk.transfer.rate"));
    }

    /// <summary>別時刻・別対象の値を補完に使わず、欠測の前後を接続しません。</summary>
    [Fact]
    public void PartialSamplesBreakCombinedTrend()
    {
        ResourceSample first = Sample(1, 2d);
        ResourceSample missing = first with { Id = Guid.NewGuid(), ObservedAt = first.ObservedAt.AddSeconds(3), Metrics = [new("disk.read.rate", "machine", 4d, "bytes/s"), new("disk.write.rate", "another", 5d, "bytes/s")] };
        ResourceSample last = first with { Id = Guid.NewGuid(), ObservedAt = first.ObservedAt.AddSeconds(6) };
        Assert.Null(DashboardMetrics.Value(missing, "disk.transfer.rate"));
        Assert.DoesNotContain("L", DashboardMetrics.Trend([first, missing, last], "disk.transfer.rate", "machine", 10d), StringComparison.Ordinal);
        Assert.Equal(0, DashboardMetrics.Value(Sample(0, 0d), "disk.transfer.rate"));
    }

    /// <summary>十進のbit/sを使い、合計・内訳とも同じ境界で単位を切り替えます。</summary>
    [Theory]
    [InlineData(0d, "0.00", "Kbps")]
    [InlineData(125d, "1.00", "Kbps")]
    [InlineData(12500d, "100.00", "Kbps")]
    [InlineData(124999d, "999.99", "Kbps")]
    [InlineData(125000d, "1.00", "Mbps")]
    [InlineData(12500000d, "100.00", "Mbps")]
    [InlineData(null, "—", "Mbps")]
    [InlineData(-1d, "—", "Mbps")]
    [InlineData(double.NaN, "—", "Mbps")]
    public void NetworkValuesUseDecimalAdaptiveUnits(double? bytes, string number, string unit)
    {
        Assert.Equal((number, unit), OverviewHostCard.NetworkThroughput(bytes));
    }

    /// <summary>同じホスト・時刻のディスク観測を作ります。</summary>
    /// <returns>方向別の取得状態を持つサンプルです。</returns>
    private static ResourceSample Sample(double? read, double? write) => new(Guid.NewGuid(), "peer", DateTimeOffset.UtcNow,
        [new("disk.read.rate", "machine", read, "bytes/s"), new("disk.write.rate", "machine", write, "bytes/s")], InstanceId: Guid.NewGuid());
}
