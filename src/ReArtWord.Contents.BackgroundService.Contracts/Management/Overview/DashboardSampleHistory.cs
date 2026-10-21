using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>配信元と画面で共通の、実体別・有限のDashboard履歴を構成します。</summary>
public static class DashboardSampleHistory
{
    /// <summary>初回履歴と差分を統合し、同じ実体の6分・121点だけを残します。</summary>
    /// <returns>重複せず時刻順に並んだ履歴です。</returns>
    public static ResourceSample[] Merge(IEnumerable<ResourceSample> previous, IEnumerable<ResourceSample> incoming, Guid instance, DateTimeOffset now)
        => previous.Concat(incoming).Where(sample => sample.InstanceId == instance && now.AddMinutes(-6) <= sample.ObservedAt)
            .DistinctBy(sample => sample.Id).OrderBy(sample => sample.ObservedAt).TakeLast(121).ToArray();

    /// <summary>概要カード・グラフが使う計測値だけを保持し、プロセスやGPUエンジン別の診断を複製しません。</summary>
    /// <returns>詳細表示を含むDashboardに必要なサンプルです。</returns>
    public static ResourceSample Project(ResourceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return sample with
        {
            Metrics = sample.Metrics.Where(metric => metric.Name is "cpu.usage" or "cpu.cores" or "cpu.threads" or "cpu.name" or "cpu.temperature"
            or "memory.usage" or "memory.used" or "memory.total" or "gpu.usage" or "gpu.memory.usage" or "gpu.memory.used" or "gpu.memory.total"
            or "gpu.memory.shared.used" or "gpu.memory.shared.limit"
            || (metric.Target is "machine" && metric.Name is "disk.read.rate" or "disk.write.rate" or "network.received.rate" or "network.sent.rate")
            || (metric.Name is "network.received" or "network.sent" && string.Equals(metric.Target, sample.SelectedInterfaceId, StringComparison.Ordinal))).ToArray()
        };
    }
}
