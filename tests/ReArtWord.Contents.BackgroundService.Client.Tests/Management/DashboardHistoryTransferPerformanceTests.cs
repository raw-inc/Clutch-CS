using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>27Peer相当の有限履歴で、配信と復元を合わせた割当・処理時間の増加を測定します。</summary>
public sealed class DashboardHistoryTransferPerformanceTests(ITestOutputHelper output)
{
    /// <summary>実JSONの往復を比較し、容量削減が復元時の割当増加にならないことを確認します。</summary>
    [Theory]
    [Trait("Category", "Performance")]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(121)]
    public void FullWindowTransferReducesPayloadAndAllocations(int count)
    {
        ResourceSample template = new(Guid.NewGuid(), "peer", DateTimeOffset.UtcNow.AddMinutes(-6),
            Enumerable.Range(0, 25).Select(index => new ResourceMetric("metric-" + index, "adapter-" + index, index, "bytes/s", Label: "Simulated hardware adapter for dashboard history")).ToArray(),
            InstanceId: Guid.NewGuid(), SelectedInterfaceId: "nic");
        ResourceSample[] samples = Enumerable.Range(0, count).Select(index => template with { Id = Guid.NewGuid(), ObservedAt = template.ObservedAt.AddSeconds(index * 3) }).ToArray();
        _ = RoundTrip(samples, false);
        _ = RoundTrip(samples, true);
        (long Bytes, long Allocated, double Milliseconds) original = Measure(samples, false);
        (long Bytes, long Allocated, double Milliseconds) compact = Measure(samples, true);
        output.WriteLine(JsonSerializer.Serialize(new { count, peers = 27, rounds = 5,
            original = new { original.Bytes, original.Allocated, original.Milliseconds },
            compact = new { compact.Bytes, compact.Allocated, compact.Milliseconds } }, ContractJson.Options));
        if (1 < count)
        {
            Assert.InRange(compact.Bytes, 1, original.Bytes / 5);
            Assert.InRange(compact.Allocated, 1, original.Allocated);
        }
    }

    /// <summary>取得を繰り返しても、表示系列が過去の全取得件数へ拡大しません。</summary>
    [Fact]
    public void RepeatedUpdatesKeepOnlyTheCurrentWindow()
    {
        Guid instance = Guid.NewGuid();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        ResourceSample[] history = [];
        for (int index = 0; index < 2000; index++)
        {
            DateTimeOffset now = start.AddSeconds(index * 3);
            ResourceSample sample = new(Guid.NewGuid(), "peer", now, [new("disk.read.rate", "machine", index, "bytes/s")], InstanceId: instance);
            history = DashboardSampleHistory.Merge(history, [sample], instance, now);
            Assert.InRange(history.Length, 1, 121);
        }
        Assert.Equal(121, history.Length);
        Assert.Equal(1999, history[^1].Metrics[0].Value);
        Assert.Equal(start.AddSeconds(1879 * 3), history[0].ObservedAt);
        Assert.Equal(121, DashboardSampleSeries.Decode(DashboardSampleSeries.Encode(history)).Count);
    }

    /// <summary>外部I/Oや強制GCを使わず、同じスレッドの処理と累積割当を測定します。</summary>
    /// <returns>5巡・各27Peer分のバイト数、累積割当、所要時間です。</returns>
    private static (long Bytes, long Allocated, double Milliseconds) Measure(ResourceSample[] samples, bool compact)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), bytes = 0;
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < 5 * 27; iteration++)
        {
            bytes += RoundTrip(samples, compact);
        }
        return (bytes, GC.GetAllocatedBytesForCurrentThread() - allocated, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>送信側の変換・JSON配信・受信側の復元を同じ区間に含めます。</summary>
    /// <returns>一Peer分の配信バイト数です。</returns>
    private static int RoundTrip(ResourceSample[] samples, bool compact)
    {
        byte[] json;
        IReadOnlyList<ResourceSample> restored;
        if (compact && 1 < samples.Length)
        {
            json = JsonSerializer.SerializeToUtf8Bytes(DashboardSampleSeries.Encode(samples), ContractJson.Options);
            restored = DashboardSampleSeries.Decode(JsonSerializer.Deserialize<List<DashboardSampleSeries>>(json, ContractJson.Options)!);
        }
        else
        {
            json = JsonSerializer.SerializeToUtf8Bytes(samples, ContractJson.Options);
            restored = JsonSerializer.Deserialize<ResourceSample[]>(json, ContractJson.Options)!;
        }
        Assert.Equal(samples.Length, restored.Count);
        Assert.Equal(samples[^1].Id, restored[^1].Id);
        return json.Length;
    }
}
