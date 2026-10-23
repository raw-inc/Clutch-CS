using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>カウンターの別名が多い場合も、集計のたびに大量の一時文字列を生成しないことを確認します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class GpuCounterAllocationTests(ITestOutputHelper output)
{
    /// <summary>同じ実体の重複をまとめ、入力件数に比例した文字列分割を避けます。</summary>
    /// <param name="memory">専用メモリの集計か、エンジン使用率の集計かです。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedCounterNamesKeepAllocationsBounded(bool memory)
    {
        GpuAdapterIdentity[] adapters = [new("luid_0x00000000_0x00000001", "GPU", 0x1002)];
        string instance = "pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_Video_Decode#1";
        if (memory)
        {
            instance = adapters[0].Luid + "_phys_0#1";
        }
        PerformanceCounterValue[] counters = Enumerable.Repeat(new PerformanceCounterValue(instance, 37), 10000).ToArray();
        _ = Aggregate(memory, adapters, counters);
        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<ResourceMetric> metrics = Aggregate(memory, adapters, counters);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Allocated bytes: {allocated}");
        Assert.Equal(37d, metrics[0].Value);
        Assert.InRange(allocated, 0, 262144);
    }

    /// <summary>どちらも同じ観測値を集計し、測定区間では入力を作り直しません。</summary>
    /// <returns>実際の集計結果です。</returns>
    private static IReadOnlyList<ResourceMetric> Aggregate(bool memory, IReadOnlyList<GpuAdapterIdentity> adapters, IReadOnlyList<PerformanceCounterValue> counters)
    {
        if (memory)
        {
            return GpuMemoryAggregation.Aggregate(adapters, counters, null);
        }
        return GpuEngineAggregation.Aggregate(counters, adapters);
    }
}
