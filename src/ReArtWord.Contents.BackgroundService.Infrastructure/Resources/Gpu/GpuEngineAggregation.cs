using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Gpu;

/// <summary>同じエンジンを二重加算せず、接続実装の最繁忙エンジンを全体利用率とします。</summary>
internal static class GpuEngineAggregation
{
    /// <summary>同じpid・物理エンジンの別名を最大値でまとめ、異なるエンジン間は加算しません。</summary>
    /// <returns>接続実装別の測定値。新規カウンターの欠測はnullのままです。</returns>
    internal static IReadOnlyList<ResourceMetric> Aggregate(IReadOnlyList<PerformanceCounterValue> counters, IReadOnlyList<GpuAdapterIdentity> adapters)
    {
        List<ResourceMetric> result = [];
        foreach (GpuAdapterIdentity adapter in adapters)
        {
            Dictionary<(uint Physical, uint Engine, uint Process), double?> unique = [];
            foreach (PerformanceCounterValue counter in counters)
            {
                if (!TryParse(counter.Instance, out string? luid, out uint physical, out uint engine, out uint process) || !string.Equals(luid, adapter.Luid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                double? value = counter.Value;
                if (value is not null && (!double.IsFinite(value.Value) || value < 0 || 100 < value))
                {
                    value = null;
                }
                (uint Physical, uint Engine, uint Process) key = (physical, engine, process);
                if (unique.TryGetValue(key, out double? previous) && previous is not null)
                {
                    value = Math.Max(previous.Value, value ?? previous.Value);
                }
                unique[key] = value;
            }
            if (unique.Count == 0)
            {
                result.Add(new("gpu.usage", adapter.Luid + "_phys_0", null, "percent", "resource.gpuNoEngineSample", adapter.Name));
            }
            foreach (IGrouping<uint, KeyValuePair<(uint Physical, uint Engine, uint Process), double?>> physical in unique.GroupBy(pair => pair.Key.Physical))
            {
                double? usage = null;
                string? reason = "resource.gpuIncompleteEngineSample";
                if (physical.All(pair => pair.Value is not null))
                {
                    usage = physical.GroupBy(pair => pair.Key.Engine).Max(engine => Math.Min(100, engine.Sum(pair => pair.Value!.Value)));
                    reason = null;
                }
                result.Add(new("gpu.usage", adapter.Luid + "_phys_" + physical.Key.ToString(CultureInfo.InvariantCulture), usage, "percent", reason, adapter.Name));
            }
        }
        return result;
    }

    /// <summary>公開カウンター実体に含まれるLUID・physical・エンジン・pidを識別します。</summary>
    /// <returns>必要な識別部がすべて整数として読めるかです。</returns>
    private static bool TryParse(string instance, out string? luid, out uint physical, out uint engine, out uint process)
    {
        luid = null;
        physical = engine = process = 0;
        string[] parts = instance.Split('_');
        string pidField = "pid", luidField = "luid", physicalField = "phys", engineField = "eng", hex = "0x";
        if (parts.Length < 10 || !string.Equals(parts[0], pidField, StringComparison.OrdinalIgnoreCase) || !string.Equals(parts[2], luidField, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[5], physicalField, StringComparison.OrdinalIgnoreCase) || !string.Equals(parts[7], engineField, StringComparison.OrdinalIgnoreCase)
            || !parts[3].StartsWith(hex, StringComparison.OrdinalIgnoreCase) || !parts[4].StartsWith(hex, StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out process) || !uint.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out physical)
            || !uint.TryParse(parts[8], NumberStyles.None, CultureInfo.InvariantCulture, out engine)
            || !uint.TryParse(parts[3].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high) || !uint.TryParse(parts[4].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low))
        {
            return false;
        }
        luid = string.Create(CultureInfo.InvariantCulture, $"luid_0x{high:X8}_0x{low:X8}");
        return true;
    }
}
