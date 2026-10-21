using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Targets;

/// <summary>採取済みの値だけでリソースを比較し、欠測と測定値を区別します。</summary>
public static class PcResourceComparison
{
    /// <summary>PC選択に使用できる観測の有効期間です。</summary>
    public static TimeSpan MaximumAge => TimeSpan.FromSeconds(10);

    /// <summary>指定PCの観測を照合し、比較に使用する値を返します。</summary>
    /// <returns>比較値、または値を使用できない理由。</returns>
    public static PcSelectionObservation Read(PeerInfo peer, ResourceSample? sample, PcResourceChoice choice, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(choice);
        string unit = choice.UsedBytes ? "bytes"
                                       : "percent";
        PcSelectionObservation result = new(peer.HostId, peer.PeerId, peer.InstanceId, choice.Resource, null, unit, sample?.ObservedAt, choice.GpuId);
        if (sample is null || sample.InstanceId != peer.InstanceId || !string.Equals(sample.PeerId, peer.PeerId, StringComparison.Ordinal))
        {
            return result with { Reason = "selection.observationMissing" };
        }
        if (MaximumAge < now - sample.ObservedAt || now < sample.ObservedAt)
        {
            return result with { Reason = "selection.observationExpired" };
        }
        bool gpu = choice.Resource is EPcResource.Gpu or EPcResource.Vram;
        string metric = choice.Resource switch
        {
            EPcResource.Cpu => "cpu.usage",
            EPcResource.Ram => choice.UsedBytes ? "memory.used"
                                                : "memory.usage",
            EPcResource.Gpu => "gpu.usage",
            EPcResource.Vram => choice.UsedBytes ? "gpu.memory.used"
                                                 : "gpu.memory.usage",
            _ => string.Empty,
        };
        ResourceMetric[] matches = sample.Metrics.Where(item => string.Equals(item.Name, metric, StringComparison.Ordinal)
            && (!gpu || choice.GpuId is null || string.Equals(item.Target, choice.GpuId, StringComparison.Ordinal))).ToArray();
        if (choice.Resource == EPcResource.Vram && !choice.UsedBytes && matches.Length == 0)
        {
            matches = VramPercentages(sample, choice.GpuId);
        }
        if (matches.Length == 0)
        {
            return result with { Reason = "selection.metricMissing" };
        }
        if (matches.Any(item => item.Value is not double value || !double.IsFinite(value) || value < 0
            || (!choice.UsedBytes && 100 < value) || item.Reason is not null || !string.Equals(item.Unit, unit, StringComparison.Ordinal)))
        {
            return result with { Reason = "selection.metricUnavailable" };
        }
        ResourceMetric selected = matches.OrderByDescending(item => item.Value).ThenBy(item => item.Target, StringComparer.Ordinal).First();
        return result with { Value = selected.Value, GpuId = gpu ? selected.Target
                                                                 : null };
    }

    /// <summary>使用率を返さない採取元では、同じGPUの容量と使用量から算出します。</summary>
    /// <returns>専用VRAMだけの使用率。</returns>
    private static ResourceMetric[] VramPercentages(ResourceSample sample, string? gpuId)
    {
        string usedName = "gpu.memory.used";
        string totalName = "gpu.memory.total";
        return sample.Metrics.Where(item => string.Equals(item.Name, usedName, StringComparison.Ordinal)
            && (gpuId is null || string.Equals(item.Target, gpuId, StringComparison.Ordinal))).Select(used =>
        {
            ResourceMetric? total = sample.Metrics.FirstOrDefault(item => string.Equals(item.Name, totalName, StringComparison.Ordinal) && string.Equals(item.Target, used.Target, StringComparison.Ordinal));
            double? percent = null;
            string? reason = "selection.metricUnavailable";
            if (used.Reason is null && total?.Reason is null && used.Value is double bytes && total?.Value is double capacity
                && double.IsFinite(bytes) && double.IsFinite(capacity) && 0 <= bytes && 0 < capacity && bytes <= capacity)
            {
                percent = bytes * 100 / capacity;
                reason = null;
            }
            return new ResourceMetric("gpu.memory.usage", used.Target, percent, "percent", reason);
        }).ToArray();
    }
}
