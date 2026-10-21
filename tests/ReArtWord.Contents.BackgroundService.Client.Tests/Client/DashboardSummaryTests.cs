using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>概要で選ぶGPUと、専用メモリーの実測推移を検証します。</summary>
public sealed class DashboardSummaryTests
{
    /// <summary>現在の負荷や使用量ではなく、専用メモリーの総容量でGPUを選びます。</summary>
    [Fact]
    public void LargestGpuUsesDedicatedCapacityAndKeepsMissingUsageUnknown()
    {
        ResourceSample sample = Sample([
            new("gpu.memory.total", "integrated", 0, "bytes"),
            new("gpu.memory.shared.limit", "integrated", 64000, "bytes"),
            new("gpu.memory.total", "small", 8000, "bytes"),
            new("gpu.memory.used", "small", 7000, "bytes"),
            new("gpu.usage", "small", 90, "percent"),
            new("gpu.memory.total", "large", 24000, "bytes"),
            new("gpu.memory.used", "large", 1000, "bytes"),
        ]);
        ResourceMetric selected = Assert.IsType<ResourceMetric>(DashboardMetrics.LargestDedicatedGpu(sample));
        Assert.Equal("large", selected.Target);
        Assert.Null(DashboardMetrics.Value(sample, "gpu.usage", selected.Target));
        Assert.Equal(1000d / 24000 * 100, DashboardMetrics.Value(sample, "gpu.memory.usage", selected.Target));
    }

    /// <summary>欠測や不正な容量を比較対象にせず、同容量の選択は観測順に依存しません。</summary>
    [Fact]
    public void EqualCapacitiesRemainStableAndZeroCapacityIsKnown()
    {
        ResourceSample sample = Sample([
            new("gpu.memory.total", "unknown", null, "bytes"),
            new("gpu.memory.total", "invalid", double.NaN, "bytes"),
            new("gpu.memory.total", "infinite", double.PositiveInfinity, "bytes"),
            new("gpu.memory.total", "negative", -1, "bytes"),
        ]);
        Assert.Null(DashboardMetrics.LargestDedicatedGpu(sample));
        Assert.Null(DashboardMetrics.LargestDedicatedGpu(null));
        sample = sample with { Metrics = [.. sample.Metrics, new("gpu.memory.total", "z", 0, "bytes"), new("gpu.memory.total", "a", 0, "bytes")] };
        Assert.Equal("a", DashboardMetrics.LargestDedicatedGpu(sample)!.Target);
        Assert.Equal("a", DashboardMetrics.LargestDedicatedGpu(sample with { Metrics = sample.Metrics.Reverse().ToArray() })!.Target);
    }

    /// <summary>更新前後の同名・同容量GPUが並んでも、実測できる識別子を表示に使用します。</summary>
    [Theory]
    [InlineData(57.7, 4024594432d)]
    [InlineData(0d, 0d)]
    [InlineData(57.7, null)]
    [InlineData(null, 4024594432d)]
    public void EqualCapacityGpuWithMeasurementsOutranksAnUnavailableIdentity(double? usage, double? memory)
    {
        string oldTarget = "luid_0x00000000_0x0001F0C4_phys_0", liveTarget = "luid_0x0000000D_0xA8A44BB1_phys_0";
        string label = "NVIDIA GeForce RTX 5080";
        ResourceSample sample = Sample([
            new("gpu.memory.total", oldTarget, 16753098752d, "bytes", Label: label),
            new("gpu.usage", oldTarget, null, "percent", "resource.gpuNoEngineSample", label),
            new("gpu.memory.used", oldTarget, null, "bytes", "resource.gpuMemoryUnavailable", label),
            new("gpu.memory.total", liveTarget, 16753098752d, "bytes", Label: label),
            new("gpu.usage", liveTarget, usage, "percent", Label: label),
            new("gpu.memory.used", liveTarget, memory, "bytes", Label: label),
        ]);
        ResourceMetric selected = Assert.IsType<ResourceMetric>(DashboardMetrics.LargestDedicatedGpu(sample));
        Assert.Equal(liveTarget, selected.Target);
        Assert.Equal(usage, DashboardMetrics.Value(sample, "gpu.usage", selected.Target));
        Assert.Equal(memory, DashboardMetrics.Value(sample, "gpu.memory.used", selected.Target));
        Assert.Equal(liveTarget, DashboardMetrics.LargestDedicatedGpu(sample with { Metrics = sample.Metrics.Reverse().ToArray() })!.Target);
        Assert.Equal(2, sample.Metrics.Count(metric => metric.Name is "gpu.memory.total"));
    }

    /// <summary>両方が有効なら負荷の高低で表示対象を入れ替えず、同名でも独立したGPUとして維持します。</summary>
    [Theory]
    [InlineData(0d, 99d)]
    [InlineData(99d, 0d)]
    public void HealthyEqualCapacityGpusStayStableRegardlessOfCurrentLoad(double firstUsage, double secondUsage)
    {
        ResourceSample sample = Sample([
            new("gpu.memory.total", "a", 16000, "bytes", Label: "Same GPU"), new("gpu.usage", "a", firstUsage, "percent"), new("gpu.memory.used", "a", 2000, "bytes"),
            new("gpu.memory.total", "z", 16000, "bytes", Label: "Same GPU"), new("gpu.usage", "z", secondUsage, "percent"), new("gpu.memory.used", "z", 12000, "bytes"),
        ]);
        Assert.Equal("a", DashboardMetrics.LargestDedicatedGpu(sample)!.Target);
        Assert.Equal("a", DashboardMetrics.LargestDedicatedGpu(sample with { Metrics = sample.Metrics.Reverse().ToArray() })!.Target);
    }

    /// <summary>不正な使用率・使用量を、表示対象を優先する根拠にしません。</summary>
    [Theory]
    [InlineData(double.NaN, double.PositiveInfinity)]
    [InlineData(-1d, -1d)]
    [InlineData(101d, 16001d)]
    public void InvalidMeasurementsDoNotOutrankAValidZero(double usage, double memory)
    {
        ResourceSample sample = Sample([
            new("gpu.memory.total", "a", 16000, "bytes"), new("gpu.usage", "a", usage, "percent"), new("gpu.memory.used", "a", memory, "bytes"),
            new("gpu.memory.total", "z", 16000, "bytes"), new("gpu.usage", "z", 0, "percent"), new("gpu.memory.used", "z", 0, "bytes"),
        ]);
        Assert.Equal("z", DashboardMetrics.LargestDedicatedGpu(sample)!.Target);
    }

    /// <summary>使用率がない計測元でも、同じGPU・同じ時点の使用量と総容量から描画できます。</summary>
    [Fact]
    public void DedicatedMemoryTrendUsesMatchingGpuAndPreservesGaps()
    {
        ResourceSample first = Sample([new("gpu.memory.used", "a", 2, "bytes"), new("gpu.memory.total", "a", 8, "bytes")]);
        ResourceSample missing = first with { Id = Guid.NewGuid(), ObservedAt = first.ObservedAt.AddSeconds(3), Metrics = [new("gpu.memory.used", "a", 4, "bytes"), new("gpu.memory.total", "b", 8, "bytes")] };
        ResourceSample last = first with { Id = Guid.NewGuid(), ObservedAt = first.ObservedAt.AddSeconds(6), Metrics = [new("gpu.memory.used", "a", 4, "bytes"), new("gpu.memory.total", "a", 8, "bytes")] };
        Assert.Equal(25, DashboardMetrics.Value(first, "gpu.memory.usage", "a"));
        Assert.Null(DashboardMetrics.Value(missing, "gpu.memory.usage", "a"));
        Assert.Contains("L", DashboardMetrics.Trend([first, last], "gpu.memory.usage", "a"), StringComparison.Ordinal);
        Assert.DoesNotContain("L", DashboardMetrics.Trend([first, missing, last], "gpu.memory.usage", "a"), StringComparison.Ordinal);
    }

    /// <summary>ゼロ容量や容量を超える使用量を正常な使用率にしません。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 8)]
    [InlineData(-1, 8)]
    public void InvalidMemorySamplesDoNotBecomeZeroUsage(double used, double total)
    {
        Assert.Null(DashboardMetrics.Value(Sample([new("gpu.memory.used", "a", used, "bytes"), new("gpu.memory.total", "a", total, "bytes")]), "gpu.memory.usage", "a"));
    }

    /// <summary>一つの実体から得た計測値を作ります。</summary>
    /// <returns>履歴の比較に使うサンプルです。</returns>
    private static ResourceSample Sample(IReadOnlyList<ResourceMetric> metrics) => new(Guid.NewGuid(), "peer", DateTimeOffset.UtcNow, metrics, InstanceId: Guid.NewGuid());
}
