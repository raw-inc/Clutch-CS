using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Gpu;

/// <summary>接続実装の専用GPU memoryと共有system memoryを足し合わせずに表します。</summary>
internal static class GpuMemoryAggregation
{
    /// <summary>OS全体の接続実装カウンターとDXGIの容量を、同じLUIDだけで対応付けます。</summary>
    /// <returns>専用使用量・容量・率と、共有使用量・共有上限です。</returns>
    internal static IReadOnlyList<ResourceMetric> Aggregate(IReadOnlyList<GpuAdapterIdentity> adapters, IReadOnlyList<PerformanceCounterValue>? dedicated, IReadOnlyList<PerformanceCounterValue>? shared)
    {
        List<ResourceMetric> result = [];
        foreach (GpuAdapterIdentity adapter in adapters)
        {
            string target = adapter.Luid + "_phys_0";
            double? used = Read(dedicated, target);
            double? sharedUsed = Read(shared, target);
            double? total = adapter.DedicatedBytes;
            double? limit = adapter.SharedLimitBytes;
            double? percent = null;
            if (used is not null && total is > 0)
            {
                percent = 100 * used / total;
            }
            result.Add(Metric("gpu.memory.used", target, used, "bytes", adapter.Name));
            result.Add(Metric("gpu.memory.total", target, total, "bytes", adapter.Name));
            result.Add(Metric("gpu.memory.usage", target, percent, "percent", adapter.Name));
            result.Add(Metric("gpu.memory.shared.used", target, sharedUsed, "bytes", adapter.Name));
            result.Add(Metric("gpu.memory.shared.limit", target, limit, "bytes", adapter.Name));
        }
        return result;
    }

    /// <summary>同じ接続実装の別名は加算せず、無効値・欠測をゼロへ変換しません。</summary>
    /// <returns>OSが返したバイト数、取得不能はnullです。</returns>
    private static double? Read(IReadOnlyList<PerformanceCounterValue>? counters, string target)
    {
        double? result = null;
        foreach (PerformanceCounterValue counter in counters ?? [])
        {
            string name = counter.Instance.Split('#')[0];
            if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase) && counter.Value is double value && double.IsFinite(value) && 0 <= value)
            {
                result = Math.Max(result ?? 0, value);
            }
        }
        return result;
    }

    /// <summary>欠測理由と単位を保持します。</summary>
    /// <returns>GPUに対応付けたmetricです。</returns>
    private static ResourceMetric Metric(string name, string target, double? value, string unit, string label)
    {
        string? reason = null;
        if (value is null)
        {
            reason = "resource.gpuMemoryUnavailable";
        }
        return new(name, target, value, unit, reason, label);
    }
}
