using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>ホスト合計の初回・リセット・機器変更と、実転送量の換算を確認します。</summary>
public sealed class ThroughputCounterTests
{
    /// <summary>累積値の合計ではなく、各機器の差分を実時間で割ります。</summary>
    [Fact]
    public void AggregatesDeviceDeltasAndDistinguishesIdleFromMissing()
    {
        CumulativeThroughput probe = new("read", "write");
        Assert.All(probe.Capture([new("a", 1000, 3000), new("b", 4000, 8000)], TimeSpan.Zero), metric => Assert.Null(metric.Value));
        IReadOnlyList<ResourceMetric> active = probe.Capture([new("b", 7000, 17000), new("a", 4000, 6000)], TimeSpan.FromSeconds(3));
        Assert.Equal(2000, active[0].Value);
        Assert.Equal(4000, active[1].Value);
        Assert.All(active, metric =>
        {
            Assert.Equal("bytes/s", metric.Unit);
            Assert.Null(metric.Reason);
        });
        Assert.All(probe.Capture([new("a", 4000, 6000), new("b", 7000, 17000)], TimeSpan.FromSeconds(6)), metric => Assert.Equal(0, metric.Value));
        Assert.All(probe.Capture(null, TimeSpan.FromSeconds(9)), metric =>
        {
            Assert.Null(metric.Value);
            Assert.NotNull(metric.Reason);
        });
        Assert.All(probe.Capture([new("a", 5000, 7000)], TimeSpan.FromSeconds(12)), metric => Assert.Null(metric.Value));
        Assert.Equal(100, probe.Capture([new("a", 5300, 7300)], TimeSpan.FromSeconds(15))[0].Value);
    }

    /// <summary>別機器の増分でリセットを隠したり、長い欠測や時計逆行を速度にしません。</summary>
    [Theory]
    [InlineData("reset", 3)]
    [InlineData("replace", 3)]
    [InlineData("add", 3)]
    [InlineData("remove", 3)]
    [InlineData("duplicate", 3)]
    [InlineData("normal", 0)]
    [InlineData("normal", -1)]
    [InlineData("normal", 31)]
    public void InvalidComparisonsRemainUnknown(string change, int seconds)
    {
        CumulativeThroughput probe = new("read", "write");
        _ = probe.Capture([new("a", 100, 100), new("b", 100, 100)], TimeSpan.Zero);
        TransferCounter[] current = change switch
        {
            "reset" => [new("a", 1, 1), new("b", 1000, 1000)],
            "replace" => [new("c", 200, 200), new("b", 200, 200)],
            "add" => [new("a", 200, 200), new("b", 200, 200), new("c", 1000, 1000)],
            "remove" => [new("a", 200, 200)],
            "duplicate" => [new("a", 200, 200), new("a", 200, 200)],
            _ => [new("a", 200, 200), new("b", 200, 200)],
        };
        Assert.All(probe.Capture(current, TimeSpan.FromSeconds(seconds)), metric => Assert.Null(metric.Value));
    }

    /// <summary>NICカウンターはdoubleへ変換する前に64bit差分を取り、微小な通信も保持します。</summary>
    [Fact]
    public void LargeLifetimeCountersRetainSmallDeltas()
    {
        CumulativeThroughput probe = new("read", "write");
        ulong total = 1UL << 60;
        _ = probe.Capture([new("nic", total, total)], TimeSpan.Zero);
        IReadOnlyList<ResourceMetric> rates = probe.Capture([new("nic", total + 3, total + 6)], TimeSpan.FromSeconds(3));
        Assert.Equal(1, rates[0].Value);
        Assert.Equal(2, rates[1].Value);
    }

    /// <summary>仮想・フィルター・トンネル・loopbackを物理合計へ混ぜません。</summary>
    [Theory]
    [InlineData(0, 6, false)]
    [InlineData(1, 6, true)]
    [InlineData(1, 71, true)]
    [InlineData(3, 6, false)]
    [InlineData(1, 24, false)]
    [InlineData(1, 131, false)]
    public void PhysicalNicSelectionUsesOperatingSystemFlags(byte flags, uint type, bool included)
    {
        WindowsInterfaceRow row = new() { Flags = flags, Type = type, Received = 15, Sent = 27, Identity = Guid.NewGuid(), Index = 42, Luid = 9000 };
        TransferCounter? counter = row.Counter();
        Assert.Equal(included, counter.HasValue);
        if (counter is { } value)
        {
            Assert.Equal(15UL, value.Read);
            Assert.Equal(27UL, value.Write);
            Assert.NotEqual(value.Identity, (row with { Index = 43 }).Counter()!.Value.Identity);
        }
    }

    /// <summary>Linuxのsysfsセクターは固定512byteであり、I/O件数とは区別します。</summary>
    [Fact]
    public void LinuxDiskStatsUseSectorCounts()
    {
        TransferCounter counter = LinuxTransferCounters.ParseDisk("disk", "9 2 2048 77 3 4 4096 88 0 4 5 6 7 8 9 10 11");
        Assert.Equal(1048576UL, counter.Read);
        Assert.Equal(2097152UL, counter.Write);
        Assert.Throws<FormatException>(() => LinuxTransferCounters.ParseDisk("disk", "1 2 3"));
        Assert.Throws<OverflowException>(() => LinuxTransferCounters.ParseDisk("disk", "0 0 -1 0 0 0 1 0 0 0 0"));
    }

    /// <summary>PDHの_Totalと個別ディスクを二重加算せず、未確定値はそのまま欠測にします。</summary>
    [Fact]
    public void PhysicalDiskTotalsKeepAvailabilityAndReleaseCounters()
    {
        using FakePerformanceCounterQuery read = new([new("_Total", null)], [new("_Total", 1048576), new("disk0", 1048576)], [new("_Total", double.NaN)]);
        using FakePerformanceCounterQuery write = new(null, [new("_Total", 2097152), new("disk0", 2097152)], [new("_Total", -1)]);
        using (WindowsDiskThroughput disk = new(read, write))
        {
            Assert.All(disk.Capture(), metric => Assert.Null(metric.Value));
            IReadOnlyList<ResourceMetric> current = disk.Capture();
            Assert.Equal(1048576, current[0].Value);
            Assert.Equal(2097152, current[1].Value);
            Assert.All(disk.Capture(), metric => Assert.Null(metric.Value));
        }
        Assert.True(read.Disposed);
        Assert.True(write.Disposed);
    }
}
