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
                if (!TryMatchAdapter(counter.Instance, adapter.Luid, out uint physical, out uint engine, out uint process))
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

    /// <summary>カウンター名を新しい文字列へ分割せず、指定GPUと物理エンジン・pidを識別します。</summary>
    /// <returns>識別部が有効で、指定GPUに属するかです。</returns>
    private static bool TryMatchAdapter(string instance, string expectedLuid, out uint physical, out uint engine, out uint process)
    {
        physical = engine = process = 0;
        ReadOnlySpan<char> text = instance.AsSpan();
        Span<Range> parts = stackalloc Range[10];
        string pidField = "pid", luidField = "luid", physicalField = "phys", engineField = "eng", hex = "0x";
        if (text.Split(parts, '_') < 10 || !text[parts[0]].Equals(pidField, StringComparison.OrdinalIgnoreCase) || !text[parts[2]].Equals(luidField, StringComparison.OrdinalIgnoreCase)
            || !text[parts[5]].Equals(physicalField, StringComparison.OrdinalIgnoreCase) || !text[parts[7]].Equals(engineField, StringComparison.OrdinalIgnoreCase)
            || !text[parts[3]].StartsWith(hex, StringComparison.OrdinalIgnoreCase) || !text[parts[4]].StartsWith(hex, StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(text[parts[1]], NumberStyles.None, CultureInfo.InvariantCulture, out process) || !uint.TryParse(text[parts[6]], NumberStyles.None, CultureInfo.InvariantCulture, out physical)
            || !uint.TryParse(text[parts[8]], NumberStyles.None, CultureInfo.InvariantCulture, out engine)
            || !uint.TryParse(text[parts[3]][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high) || !uint.TryParse(text[parts[4]][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low))
        {
            return false;
        }

        // 数値も直接書き込み、カウンターごとの文字列生成とボックス化を避けます。
        Span<char> luid = stackalloc char[26];
        "luid_0x".AsSpan().CopyTo(luid);
        "_0x".AsSpan().CopyTo(luid[15..]);
        return high.TryFormat(luid[7..15], out _, "X8", CultureInfo.InvariantCulture)
            && low.TryFormat(luid[18..], out _, "X8", CultureInfo.InvariantCulture)
            && luid.Equals(expectedLuid, StringComparison.OrdinalIgnoreCase);
    }
}
