using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Gpu;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>GPU エンジンの集約・差分待ち・実Windowsの観測を検証します。</summary>
public sealed class WindowsGpuProbeTests(ITestOutputHelper output)
{
    /// <summary>同じprocess/engineの別名を重複せず、別エンジンは最大値を選びます。</summary>
    [Fact]
    public void PhysicalEnginesAreNotSummedTwice()
    {
        GpuAdapterIdentity adapter = Adapter(1, "AMD", 0x1002);
        IReadOnlyList<ResourceMetric> metrics = GpuEngineAggregation.Aggregate([
            Counter(1, 10, 0, 20), Counter(1, 10, 0, 20) with { Instance = Counter(1, 10, 0, 20).Instance + "#1" },
            Counter(1, 20, 0, 30), Counter(1, 10, 1, 80),
            Counter(2, 10, 0, 12),
        ], [adapter, Adapter(2, "NVIDIA", 0x10DE)]);
        Assert.Equal(80d, metrics[0].Value);
        Assert.Equal(12d, metrics[1].Value);
        Assert.NotEqual(metrics[0].Target, metrics[1].Target);
        Assert.Equal("AMD", metrics[0].Label);
        IReadOnlyList<ResourceMetric> incomplete = GpuEngineAggregation.Aggregate([Counter(1, 10, 0, null), Counter(2, 10, 0, 12)], [adapter, Adapter(2, "NVIDIA", 0x10DE)]);
        Assert.Null(incomplete[0].Value);
        Assert.NotNull(incomplete[0].Reason);
        Assert.Equal(12d, incomplete[1].Value);
    }

    /// <summary>初回と照会 failure後の新基点はunknownで、既知NVIDIA利用率を二重に出しません。</summary>
    [Fact]
    public void WarmupFailureAndProviderOwnershipRemainExplicit()
    {
        PerformanceCounterValue[] values = [Counter(1, 10, 0, 35), Counter(2, 10, 0, 55)];
        using FakePerformanceCounterQuery query = new(values, values, null, values, values);
        using WindowsGpuProbe probe = new(query, () => [Adapter(1, "AMD", 0x1002), Adapter(2, "NVIDIA", 0x10DE)]);
        Assert.All(probe.Capture(false), metric => Assert.Null(metric.Value));
        Assert.Equal(35d, probe.Capture(false)[0].Value);
        Assert.All(probe.Capture(false), metric => Assert.Null(metric.Value));
        Assert.All(probe.Capture(false), metric => Assert.Null(metric.Value));
        ResourceMetric amd = Assert.Single(probe.Capture(true), metric => string.Equals(metric.Name, "gpu.usage", StringComparison.Ordinal));
        Assert.Equal("AMD", amd.Label);
        Assert.Equal(35d, amd.Value);
        probe.Dispose();
        Assert.True(query.Disposed);
    }

    /// <summary>標準PDH/DXGIを実採取し、測定値と取得不能の理由を検証します。</summary>
    /// <returns>初回と次回の実OS観測結果です。</returns>
    [Fact]
    public async Task ActualWindowsCounterReportsMeasurementsOrUnavailableReasonsAfterWarmup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using WindowsGpuProbe probe = WindowsGpuProbe.Create();
        IReadOnlyList<ResourceMetric> first = probe.Capture(false);
        Assert.All(first.Where(metric => string.Equals(metric.Name, "gpu.usage", StringComparison.Ordinal)), metric => Assert.Null(metric.Value));
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        IReadOnlyList<ResourceMetric> next = probe.Capture(false);
        Assert.NotEmpty(next);
        foreach (ResourceMetric metric in next)
        {
            output.WriteLine($"{metric.Label} | {metric.Target} | {metric.Name} = {metric.Value} {metric.Unit} | {metric.Reason}");
        }
        Assert.Contains(next, metric => string.Equals(metric.Name, "gpu.usage", StringComparison.Ordinal));
        if (!HostedDesktopFactAttribute.IsHostedRunner)
        {
            // GPUを持つ実機の確認では、ホステッドCIの欠測を許容する条件を適用しません。
            Assert.Contains(next, metric => metric.Value is not null);
            Assert.Contains(next, metric => string.Equals(metric.Name, "gpu.memory.used", StringComparison.Ordinal) && metric.Value is not null);
            Assert.Contains(next, metric => string.Equals(metric.Name, "gpu.memory.total", StringComparison.Ordinal) && metric.Value is > 0);
            Assert.Contains(next, metric => string.Equals(metric.Name, "gpu.memory.shared.used", StringComparison.Ordinal) && metric.Value is not null);
        }
        Assert.All(next, metric =>
        {
            if (metric.Value is double value)
            {
                Assert.True(double.IsFinite(value));
                Assert.True(0 <= value);
                string percentageUnit = "percent";
                if (string.Equals(metric.Unit, percentageUnit, StringComparison.Ordinal))
                {
                    Assert.InRange(value, 0, 100);
                }
                Assert.False(string.IsNullOrWhiteSpace(metric.Label));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(metric.Reason));
            }
        });
    }

    /// <summary>専用容量へ共有RAMを加算せず、欠測と実ゼロを区別します。</summary>
    [Fact]
    public void DedicatedAndSharedGpuMemoryStaySeparate()
    {
        GpuAdapterIdentity gpu = Adapter(1, "Discrete", 0x1002) with { DedicatedBytes = 16UL * 1073741824, SharedLimitBytes = 32UL * 1073741824 };
        GpuAdapterIdentity integrated = Adapter(2, "Integrated", 0x1002) with { DedicatedBytes = 512UL * 1048576, SharedLimitBytes = 32UL * 1073741824 };
        string target = gpu.Luid + "_phys_0";
        IReadOnlyList<ResourceMetric> memory = GpuMemoryAggregation.Aggregate([gpu, integrated],
            [new(target, 8d * 1073741824), new(target + "#1", 8d * 1073741824), new(integrated.Luid + "_phys_0", 0)], [new(target, 2d * 1073741824)]);
        Assert.Equal(50d, Assert.Single(memory, metric => string.Equals(metric.Target, target, StringComparison.Ordinal) && string.Equals(metric.Name, "gpu.memory.usage", StringComparison.Ordinal)).Value);
        Assert.Equal(16d * 1073741824, Assert.Single(memory, metric => string.Equals(metric.Target, target, StringComparison.Ordinal) && string.Equals(metric.Name, "gpu.memory.total", StringComparison.Ordinal)).Value);
        Assert.Equal(2d * 1073741824, Assert.Single(memory, metric => string.Equals(metric.Target, target, StringComparison.Ordinal) && string.Equals(metric.Name, "gpu.memory.shared.used", StringComparison.Ordinal)).Value);
        ResourceMetric zero = Assert.Single(memory, metric => string.Equals(metric.Label, integrated.Name, StringComparison.Ordinal) && string.Equals(metric.Name, "gpu.memory.used", StringComparison.Ordinal));
        Assert.Equal(0d, zero.Value);
        Assert.Null(zero.Reason);
        ResourceMetric missing = Assert.Single(memory, metric => string.Equals(metric.Label, integrated.Name, StringComparison.Ordinal) && string.Equals(metric.Name, "gpu.memory.shared.used", StringComparison.Ordinal));
        Assert.Null(missing.Value);
        Assert.NotNull(missing.Reason);
    }

    /// <summary>桁数・大文字小文字・別名の表記が異なっても、同じGPUと物理エンジンへ対応付けます。</summary>
    /// <param name="instance">OSカウンターの実体名です。</param>
    [Theory]
    [InlineData("pid_7_luid_0x00ABCDEF_0x00000001_phys_2_eng_4_engtype_3D")]
    [InlineData("PID_4294967295_LUID_0Xabcdef_0X1_PHYS_2_ENG_4294967295_engtype_Video_Decode#1")]
    [InlineData("pid_0_luid_0x00abcdef_0x1_phys_2_eng_0_")]
    public void CounterNamesPreserveAdapterAndPhysicalIdentity(string instance)
    {
        GpuAdapterIdentity adapter = new("luid_0x00ABCDEF_0x00000001", "GPU", 0x1002);
        ResourceMetric metric = Assert.Single(GpuEngineAggregation.Aggregate([new(instance, 37)], [adapter]));
        Assert.Equal(37d, metric.Value);
        Assert.Equal(adapter.Luid + "_phys_2", metric.Target);
        Assert.Null(metric.Reason);
    }

    /// <summary>不完全な識別情報を別のGPUやゼロ使用率として採用しません。</summary>
    /// <param name="instance">不正または別GPUのカウンター名です。</param>
    [Theory]
    [InlineData("")]
    [InlineData("pid_1_luid_0x0_0x1_phys_0_eng_0")]
    [InlineData("pid_-1_luid_0x0_0x1_phys_0_eng_0_3D")]
    [InlineData("pid_4294967296_luid_0x0_0x1_phys_0_eng_0_3D")]
    [InlineData("pid_1_luid_0_0x1_phys_0_eng_0_3D")]
    [InlineData("pid_1_luid_0x100000000_0x1_phys_0_eng_0_3D")]
    [InlineData("pid_1_luid_0x0_0x1_phys_-1_eng_0_3D")]
    [InlineData("pid_1_luid_0x0_0x1_phys_0_eng_x_3D")]
    [InlineData("pid_1_luid_0x0_0x2_phys_0_eng_0_3D")]
    public void InvalidOrUnrelatedCounterNamesRemainUnavailable(string instance)
    {
        ResourceMetric metric = Assert.Single(GpuEngineAggregation.Aggregate([new(instance, 37)], [Adapter(1, "GPU", 0x1002)]));
        Assert.Null(metric.Value);
        Assert.Equal("resource.gpuNoEngineSample", metric.Reason);
    }

    /// <summary>メモリカウンターの別名と大文字小文字を区別せず、最大の有効値を採用します。</summary>
    [Fact]
    public void MemoryCounterAliasesPreserveMaximumValidValue()
    {
        GpuAdapterIdentity adapter = Adapter(1, "GPU", 0x1002);
        string target = adapter.Luid + "_phys_0";
        IReadOnlyList<ResourceMetric> metrics = GpuMemoryAggregation.Aggregate([adapter],
            [new(target.ToUpperInvariant() + "#1#alias", 12), new(target, 9), new(target + "#2", double.NaN), new(target + "#3", -1), new(target + "_other", 100)], null);
        Assert.Equal(12d, Assert.Single(metrics, metric => string.Equals(metric.Name, "gpu.memory.used", StringComparison.Ordinal)).Value);
    }

    /// <summary>物理接続実装のLUIDと名前を作ります。</summary>
    /// <returns>既知接続実装です。</returns>
    private static GpuAdapterIdentity Adapter(uint low, string name, uint vendor) => new(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"luid_0x00000000_0x{low:X8}"), name, vendor);

    /// <summary>PIDとphysical エンジンを識別できるカウンターを作ります。</summary>
    /// <returns>生測定入力です。</returns>
    private static PerformanceCounterValue Counter(uint adapter, uint process, uint engine, double? usage) => new(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"pid_{process}_luid_0x00000000_0x{adapter:X8}_phys_0_eng_{engine}_engtype_3D"), usage);
}
