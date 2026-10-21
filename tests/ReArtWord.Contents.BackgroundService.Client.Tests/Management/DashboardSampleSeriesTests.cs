using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>概要履歴の配信量を削減し、元の計測値と欠測の境界を保持することを確認します。</summary>
public sealed class DashboardSampleSeriesTests
{
    /// <summary>履歴が上限まで蓄積しても、項目説明を毎回転送しません。</summary>
    [Fact]
    public void FullHistoryRoundTripsWithLessThanOneFifthOfThePayload()
    {
        ResourceSample template = Sample();
        ResourceSample[] samples = Enumerable.Range(0, 121).Select(index => template with
        {
            Id = Guid.NewGuid(), ObservedAt = template.ObservedAt.AddSeconds(index * 3),
            Metrics = template.Metrics.Select(metric => metric with { Value = index * 0.125 }).ToArray(),
        }).ToArray();
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(samples, ContractJson.Options);
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(DashboardSampleSeries.Encode(samples), ContractJson.Options);
        Assert.True(encoded.Length < original.Length / 5, $"Original: {original.Length}, encoded: {encoded.Length}");
        IReadOnlyList<ResourceSample> decoded = DashboardSampleSeries.Decode(JsonSerializer.Deserialize<List<DashboardSampleSeries>>(encoded, ContractJson.Options)!);
        Assert.Equal(original, JsonSerializer.SerializeToUtf8Bytes(decoded, ContractJson.Options));
    }

    /// <summary>NIC、実体、取得理由、項目順やGPU構成の変化を正しい位置で維持します。</summary>
    [Fact]
    public void LayoutChangesAndMissingValuesAreNotHidden()
    {
        ResourceSample sample = Sample();
        ResourceSample[] samples =
        [
            sample,
            sample with { Id = Guid.NewGuid(), InstanceId = Guid.NewGuid() },
            sample with { Id = Guid.NewGuid(), SelectedInterfaceId = "second-nic" },
            sample with { Id = Guid.NewGuid(), PeerId = "second-peer" },
            sample with { Id = Guid.NewGuid(), HistoryStored = true },
            sample with { Id = Guid.NewGuid(), HistoryPending = true },
            sample with { Id = Guid.NewGuid(), Metrics = sample.Metrics.Reverse().ToArray() },
            sample with { Id = Guid.NewGuid(), Metrics = sample.Metrics.Take(2).ToArray() },
            sample with { Id = Guid.NewGuid(), Metrics = [new("gpu.usage", "gpu", null, "%", "unavailable", "adapter")] },
            sample with { Id = Guid.NewGuid(), Metrics = [new("gpu.usage", "gpu", 0, "%", null, "adapter")] },
            sample with { Id = Guid.NewGuid(), Metrics = [new("gpu.usage", "gpu", 50, "%", null, "renamed")] },
            sample with { Id = Guid.NewGuid(), Metrics = [new("gpu.usage", "second-gpu", 10, "%", null, "renamed")] },
            sample with { Id = Guid.NewGuid(), Metrics = [new("gpu.usage", "second-gpu", 10, "bytes", null, "renamed")] },
            sample with { Id = Guid.NewGuid(), Metrics = [new("gpu.memory.used", "second-gpu", 10, "bytes", null, "renamed")] },
        ];
        IReadOnlyList<DashboardSampleSeries> encoded = DashboardSampleSeries.Encode(samples);
        Assert.Equal(samples.Length, encoded.Count);
        Assert.Equal(JsonSerializer.Serialize(samples, ContractJson.Options), JsonSerializer.Serialize(DashboardSampleSeries.Decode(encoded), ContractJson.Options));
        Assert.Empty(DashboardSampleSeries.Decode(DashboardSampleSeries.Encode([])));
    }

    /// <summary>破損した長さや過大な履歴を正常な計測として採用しません。</summary>
    [Fact]
    public void InvalidPointCountsAndMetricLayoutsAreRejected()
    {
        ResourceSample sample = Sample();
        DashboardSamplePoint invalid = new(sample.Id, sample.ObservedAt, [1]);
        Assert.Throws<JsonException>(() => DashboardSampleSeries.Decode([new(sample, [invalid])]));
        DashboardSamplePoint valid = invalid with { Values = sample.Metrics.Select(metric => metric.Value).ToArray() };
        Assert.Throws<JsonException>(() => DashboardSampleSeries.Decode([new(sample, Enumerable.Repeat(valid, 122).ToArray())]));
        Assert.Throws<JsonException>(() => DashboardSampleSeries.Decode([new(sample, Enumerable.Repeat(valid, 121).ToArray()), new(sample, [valid])]));
    }

    /// <summary>CPU・GPU・IO・NW相当の項目数と、長い実機ラベルを持つ計測を作成します。</summary>
    /// <returns>現在値と説明を持つ一つのサンプルです。</returns>
    private static ResourceSample Sample() => new(Guid.NewGuid(), "peer", DateTimeOffset.UtcNow.AddMinutes(-6),
        Enumerable.Range(0, 25).Select(index => new ResourceMetric("metric-" + index, "adapter-" + index, index, "bytes/s", Label: "Simulated hardware adapter for dashboard history")).ToArray(),
        InstanceId: Guid.NewGuid(), SelectedInterfaceId: "selected-nic");
}
