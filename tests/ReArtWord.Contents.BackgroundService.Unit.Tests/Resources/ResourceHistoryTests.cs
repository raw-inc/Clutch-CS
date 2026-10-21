using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Probes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>診断採取の容量を制限したバッファーとまとめ保存を実ファイルで検証します。</summary>
public sealed class ResourceHistoryTests
{
    /// <summary>保存間隔内の採取と履歴照会はディスクを使わず、30秒後に全区間を保存します。</summary>
    /// <returns>読取り・書込みの抑制と実測保持の検証です。</returns>
    [Fact]
    public async Task PeriodicSamplesAreBufferedWithoutDiskAccessAndPersistedTogether()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "history.json");
        JsonFileRepository repository = new(path);
        using ResourceHistory history = new(repository, "peer:p:resources", new ResourceOptions(3000, 12, 1));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ResourceSample first = Sample(now);
        Assert.True((await history.AppendAsync(first, false)).HistoryStored);
        using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (int index = 1; index < 10; index++)
            {
                ResourceSample pending = await history.AppendAsync(Sample(now.AddSeconds(index * 3)), false);
                Assert.True(pending.HistoryPending);
                Assert.False(pending.HistoryStored);
            }
            Assert.Equal(10, (await history.ReadAsync()).Count);
        }
        using (ResourceHistory before = new(repository, "peer:p:resources", new ResourceOptions(3000, 12, 1)))
        {
            Assert.Single(await before.ReadAsync());
        }
        ResourceSample persisted = await history.AppendAsync(Sample(now.AddSeconds(30)), false);
        Assert.True(persisted.HistoryStored);
        Assert.False(persisted.HistoryPending);
        using ResourceHistory restarted = new(repository, "peer:p:resources", new ResourceOptions(3000, 12, 1));
        IReadOnlyList<ResourceSample> restored = await restarted.ReadAsync();
        Assert.Equal(11, restored.Count);
        Assert.Equal(persisted.Id, restored[^1].Id);
        Assert.All(restored, sample => Assert.True(sample.HistoryStored));
    }

    /// <summary>件数を超えた採取は解放し、停止時に最後の保存待ちも排出します。</summary>
    /// <returns>有限メモリと停止時保存の検証です。</returns>
    [Fact]
    public async Task FlushPersistsBoundedPendingSamples()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "history.db"));
        using ResourceHistory history = new(repository, "peer:p:resources", new ResourceOptions(3000, 3, 1));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index < 8; index++)
        {
            await history.AppendAsync(Sample(now.AddSeconds(index * 3)), false);
        }
        Assert.Equal(3, (await history.ReadAsync()).Count);
        await history.FlushAsync();
        using ResourceHistory restarted = new(repository, "peer:p:resources", new ResourceOptions(3000, 3, 1));
        ResourceSample[] saved = (await restarted.ReadAsync()).ToArray();
        Assert.Equal(3, saved.Length);
        Assert.Equal(now.AddSeconds(21), saved[^1].ObservedAt);
        Assert.All(saved, sample => Assert.True(sample.HistoryStored && !sample.HistoryPending));
        JsonElement persisted = JsonSerializer.SerializeToElement(saved, ContractJson.Options);
        Assert.False(persisted[0].TryGetProperty("historyPending", out _));
    }

    /// <summary>CPU仕様が取得できるOSではプロセスquotaでなく機器情報を返します。</summary>
    /// <returns>実OS上の情報取得です。</returns>
    [Fact]
    public async Task CpuInformationReportsPhysicalAndLogicalCounts()
    {
        IReadOnlyList<ResourceMetric> metrics = await CpuInformation.ReadAsync();
        Assert.Equal(3, metrics.Count);
        if (OperatingSystem.IsWindows())
        {
            double? cores = metrics.Single(metric => string.Equals(metric.Name, "cpu.cores", StringComparison.Ordinal)).Value;
            double? threads = metrics.Single(metric => string.Equals(metric.Name, "cpu.threads", StringComparison.Ordinal)).Value;
            Assert.True(0 < cores && cores <= threads);
            Assert.False(string.IsNullOrWhiteSpace(metrics.Single(metric => string.Equals(metric.Name, "cpu.name", StringComparison.Ordinal)).Label));
        }
    }

    /// <summary>副作用なしで時刻境界を確認する実測試験用の構成です。</summary>
    /// <returns>一件の観測です。</returns>
    private static ResourceSample Sample(DateTimeOffset time) => new(Guid.NewGuid(), "p", time, [new("cpu.usage", "machine", 25, "percent")]);
}
