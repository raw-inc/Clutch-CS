using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>連続する同じ項目構成の説明を一度だけ転送します。保存用の履歴形式は変更しません。</summary>
/// <param name="Template">実体、NIC、保存状態、計測項目の説明を持つ先頭のサンプルです。</param>
/// <param name="Points">識別子・時刻・値を省略せず保持した計測点です。</param>
public sealed record DashboardSampleSeries(ResourceSample Template, IReadOnlyList<DashboardSamplePoint> Points)
{
    /// <summary>項目名・単位・理由・ラベルを共有し、構成が変化した位置では系列を分けます。</summary>
    /// <returns>元の順序を維持した有限の系列です。</returns>
    public static IReadOnlyList<DashboardSampleSeries> Encode(IReadOnlyList<ResourceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        List<DashboardSampleSeries> series = [];
        ResourceSample? template = null;
        List<DashboardSamplePoint> points = [];
        foreach (ResourceSample sample in samples)
        {
            if (template is null || !SameLayout(template, sample))
            {
                template = sample;
                points = [];
                series.Add(new(template, points));
            }
            points.Add(new(sample.Id, sample.ObservedAt, sample.Metrics.Select(metric => metric.Value).ToArray()));
        }
        return series;
    }

    /// <summary>画面内の既存の系列へ復元し、欠測や実体の切り替わりを平滑化しません。</summary>
    /// <returns>元の値・メタデータと同じサンプルです。不正な長さや過大な点数は拒否します。</returns>
    public static IReadOnlyList<ResourceSample> Decode(IReadOnlyList<DashboardSampleSeries> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        List<ResourceSample> samples = [];
        foreach (DashboardSampleSeries item in series)
        {
            if (121 - samples.Count < item.Points.Count)
            {
                throw new JsonException("Dashboard history exceeds its sample limit.");
            }
            foreach (DashboardSamplePoint point in item.Points)
            {
                if (point.Values.Count != item.Template.Metrics.Count)
                {
                    throw new JsonException("Dashboard values do not match their metric layout.");
                }
                ResourceMetric[] metrics = new ResourceMetric[point.Values.Count];
                for (int index = 0; index < metrics.Length; index++)
                {
                    metrics[index] = item.Template.Metrics[index] with { Value = point.Values[index] };
                }
                samples.Add(item.Template with { Id = point.Id, ObservedAt = point.ObservedAt, Metrics = metrics });
            }
        }
        return samples;
    }

    /// <summary>値・時刻・識別子以外の違いを失わないよう、項目の順序も比較します。</summary>
    /// <returns>同じテンプレートを使えるかです。</returns>
    private static bool SameLayout(ResourceSample left, ResourceSample right)
    {
        if (left.InstanceId != right.InstanceId || left.HistoryStored != right.HistoryStored || left.HistoryPending != right.HistoryPending
            || !string.Equals(left.PeerId, right.PeerId, StringComparison.Ordinal)
            || !string.Equals(left.SelectedInterfaceId, right.SelectedInterfaceId, StringComparison.Ordinal)
            || left.Metrics.Count != right.Metrics.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Metrics.Count; index++)
        {
            ResourceMetric a = left.Metrics[index], b = right.Metrics[index];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) || !string.Equals(a.Target, b.Target, StringComparison.Ordinal)
                || !string.Equals(a.Unit, b.Unit, StringComparison.Ordinal) || !string.Equals(a.Reason, b.Reason, StringComparison.Ordinal)
                || !string.Equals(a.Label, b.Label, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
