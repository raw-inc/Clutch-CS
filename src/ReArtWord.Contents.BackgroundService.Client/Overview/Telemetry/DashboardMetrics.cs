using System.Globalization;
using System.Text;
using ReArtWord.Contents.BackgroundService.Client.Overview.Hosts;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Telemetry;

/// <summary>欠測・初回・実体変更をゼロへ変えず、表示用の実測値を作ります。</summary>
internal static class DashboardMetrics
{
    /// <summary>同一PCの正常な計測元を維持し、別クラスタの採取時刻だけで履歴を入れ替えません。</summary>
    /// <returns>継続できる元の実体、または新しく確認できた計測元です。</returns>
    internal static OverviewPeer HostPeer(IReadOnlyList<OverviewPeer> peers, Guid? preferredInstance, Func<OverviewPeer, bool> isPrevious)
        => peers.FirstOrDefault(peer => peer.Peer.InstanceId == preferredInstance && !isPrevious(peer))
            ?? peers.OrderBy(isPrevious).ThenByDescending(peer => peer.Latest?.ObservedAt).First();

    /// <summary>同じ対象の有限な実測値だけを返します。</summary>
    /// <returns>取得不能ならnullです。</returns>
    internal static double? Value(ResourceSample? sample, string name, string target = "machine")
    {
        string diskTransfer = "disk.transfer.rate", networkTransfer = "network.transfer.rate";
        if (string.Equals(name, diskTransfer, StringComparison.Ordinal))
        {
            return CombinedRate(sample, "disk.read.rate", "disk.write.rate", target);
        }
        if (string.Equals(name, networkTransfer, StringComparison.Ordinal))
        {
            return CombinedRate(sample, "network.received.rate", "network.sent.rate", target);
        }
        double? value = sample?.Metrics.FirstOrDefault(metric => string.Equals(metric.Name, name, StringComparison.Ordinal) && string.Equals(metric.Target, target, StringComparison.Ordinal))?.Value;
        if (value is double number && double.IsFinite(number))
        {
            return number;
        }
        string dedicatedUsage = "gpu.memory.usage";
        if (string.Equals(name, dedicatedUsage, StringComparison.Ordinal))
        {
            double? used = Value(sample, "gpu.memory.used", target);
            double? total = Value(sample, "gpu.memory.total", target);
            if (used is >= 0 && total is > 0 && used <= total)
            {
                return 100 * (used.Value / total.Value);
            }
        }
        return null;
    }

    /// <summary>同一サンプルの両方向が観測できたときだけ合計し、片側の欠測をゼロにしません。</summary>
    /// <returns>有限で非負の合計転送量、どちらかが欠測ならnullです。</returns>
    private static double? CombinedRate(ResourceSample? sample, string first, string second, string target)
    {
        double? left = Value(sample, first, target);
        double? right = Value(sample, second, target);
        if (left is >= 0 && right is >= 0 && double.IsFinite(left.Value + right.Value))
        {
            return left.Value + right.Value;
        }
        return null;
    }

    /// <summary>最大専用容量を優先し、同容量なら有効な使用率・使用量のあるGPU、最後に識別子順で選びます。</summary>
    /// <returns>最大容量のGPUの計測項目。比較できない場合はnullです。</returns>
    internal static ResourceMetric? LargestDedicatedGpu(ResourceSample? sample)
    {
        string capacity = "gpu.memory.total";
        return sample?.Metrics.Where(metric => string.Equals(metric.Name, capacity, StringComparison.Ordinal)
                && metric.Value is >= 0 && double.IsFinite(metric.Value.Value))
            .OrderByDescending(metric => metric.Value)
            .ThenByDescending(metric => Value(sample, "gpu.usage", metric.Target) is >= 0 and <= 100)
            .ThenByDescending(metric => Value(sample, "gpu.memory.used", metric.Target) is double used && 0 <= used && used <= metric.Value)
            .ThenBy(metric => metric.Target, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>選択NICの累積バイト値だけを、同じ実体の実測間隔で速度にします。</summary>
    /// <returns>bytes/second。初回・reset・長い欠測・NIC変更はnullです。</returns>
    internal static double? NetworkRate(ResourceSample? previous, ResourceSample? current, string metric)
    {
        if (previous is null || current is null || current.InstanceId is null || current.InstanceId == Guid.Empty || current.InstanceId != previous.InstanceId
            || !string.Equals(current.PeerId, previous.PeerId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(current.SelectedInterfaceId)
            || !string.Equals(current.SelectedInterfaceId, previous.SelectedInterfaceId, StringComparison.Ordinal))
        {
            return null;
        }
        double seconds = (current.ObservedAt - previous.ObservedAt).TotalSeconds;
        double? before = Value(previous, metric, current.SelectedInterfaceId);
        double? after = Value(current, metric, current.SelectedInterfaceId);
        if (seconds <= 0 || 30 < seconds || before is null || after is null || before < 0 || after < before)
        {
            return null;
        }
        return (after.Value - before.Value) / seconds;
    }

    /// <summary>過去値や時計の不一致を現在の実測と表示しません。</summary>
    /// <returns>新鮮な観測として使えないかです。</returns>
    internal static bool IsStale(ResourceSample? sample, DateTimeOffset now) => sample is null || TimeSpan.FromSeconds(15) < now - sample.ObservedAt || TimeSpan.FromSeconds(5) < sample.ObservedAt - now;

    /// <summary>実測点だけを時間軸に配置し、欠測区間を接続しません。</summary>
    /// <returns>SVGのパス。二点未満なら空です。</returns>
    internal static string Trend(IReadOnlyList<ResourceSample> samples, string metric, string target, double maximum = 100)
    {
        if (samples.Count < 2 || !double.IsFinite(maximum) || maximum <= 0)
        {
            return string.Empty;
        }
        ResourceSample last = samples[^1];
        DateTimeOffset start = last.ObservedAt.AddMinutes(-5);
        long startMilliseconds = last.ObservedAt.ToUnixTimeMilliseconds() - 300000;
        StringBuilder result = new();
        DateTimeOffset? previous = null;
        int count = 0;
        foreach (ResourceSample sample in samples.Where(sample => start.AddMinutes(-1) <= sample.ObservedAt && sample.InstanceId == last.InstanceId))
        {
            double? value = Value(sample, metric, target);
            if (value is null || value < 0 || maximum < value)
            {
                previous = null;
                continue;
            }
            string command = "M";
            if (previous is not null && sample.ObservedAt - previous.Value <= TimeSpan.FromSeconds(15))
            {
                command = "L";
            }
            double x = (sample.ObservedAt.ToUnixTimeMilliseconds() - startMilliseconds) / 1000d;
            double y = 100 - value.Value / maximum * 100;
            result.Append(CultureInfo.InvariantCulture, $"{command}{x:F3},{y:F2} ");
            previous = sample.ObservedAt;
            count++;
        }
        if (count < 2)
        {
            return string.Empty;
        }
        return result.ToString();
    }

    /// <summary>同じ実体の表示期間だけを使い、転送量用の目盛り上限を切り上げます。</summary>
    /// <returns>最低一表示単位を確保した、bytes/sの有限な上限です。</returns>
    internal static double ThroughputMaximum(IReadOnlyList<ResourceSample> samples, string metric, double divisor)
    {
        double peak = 1;
        if (samples.Count != 0)
        {
            ResourceSample last = samples[^1];
            foreach (ResourceSample sample in samples)
            {
                if (sample.InstanceId == last.InstanceId && last.ObservedAt.AddMinutes(-6) <= sample.ObservedAt && sample.ObservedAt <= last.ObservedAt
                    && Value(sample, metric) is double value && 0 <= value)
                {
                    peak = Math.Max(peak, value / divisor);
                }
            }
        }
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        return Math.Ceiling(peak / magnitude) * magnitude * divisor;
    }

}
