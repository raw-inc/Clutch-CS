using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Nodes.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>実リソース値とワークフロー未設定時の有限診断履歴を検証します。</summary>
public sealed class ResourceServiceTests
{
    /// <summary>実CPU・メモリを取得し、GPU取得不能をゼロに丸めません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ActualMetricsPreserveUnavailableStateAndBoundHistory()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "resource.db"));
        using ResourceService service = new(repository, "peer", new ResourceOptions(10, 3, 1));
        Assert.Null(service.Latest);
        ResourceSample first = await service.CaptureAsync();
        Assert.Same(first, service.Latest);
        Assert.True(first.HistoryStored);
        ResourceMetric cpuInitial = Assert.Single(first.Metrics, metric => string.Equals(metric.Name, "cpu.usage", StringComparison.Ordinal));
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Null(cpuInitial.Value);
            Assert.NotNull(cpuInitial.Reason);
        }
        ResourceMetric memory = Assert.Single(first.Metrics, metric => string.Equals(metric.Name, "memory.total", StringComparison.Ordinal));
        Assert.True(0 < memory.Value);
        ResourceMetric[] gpus = first.Metrics.Where(metric => string.Equals(metric.Name, "gpu.usage", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(gpus);
        Assert.All(gpus, gpu =>
        {
            Assert.Null(gpu.Value);
            Assert.NotNull(gpu.Reason);
        });
        await Task.Delay(30).ConfigureAwait(true);
        ResourceSample latest = first;
        for (int index = 0; index < 5; index++)
        {
            latest = await service.CaptureAsync();
        }
        IReadOnlyList<ResourceSample> history = await service.HistoryAsync();
        Assert.Equal(3, history.Count);
        Assert.Equal(latest.Id, history[^1].Id);
        Assert.All(history, sample => Assert.True(sample.HistoryStored));
        ResourceMetric cpu = Assert.Single(latest.Metrics, metric => string.Equals(metric.Name, "cpu.usage", StringComparison.Ordinal));
        if (cpu.Value.HasValue)
        {
            Assert.InRange(cpu.Value.Value, 0, 100);
        }
        else
        {
            Assert.NotNull(cpu.Reason);
        }
        using ResourceService restarted = new(repository, "peer", new ResourceOptions(10, 3, 1));
        Assert.Equal(latest.Id, (await restarted.HistoryAsync())[^1].Id);
        RepositorySnapshot shared = await repository.ExportAsync();
        Assert.Empty(shared.Entries);
        Assert.Empty(shared.Receipts);
    }

    /// <summary>管理照会は保存先が使用中でも採取・永続書込みを起こしません。</summary>
    /// <returns>実ファイルを排他した最新実測読取りの検証です。</returns>
    [Fact]
    public async Task LatestReadDoesNotTouchHistoryStorageOrRecapture()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "resources.json");
        JsonFileRepository repository = new(path);
        using ResourceService service = new(repository, "peer", new ResourceOptions());
        ResourceSample captured = await service.CaptureAsync();
        using FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        for (int index = 0; index < 20; index++)
        {
            Assert.Same(captured, service.Latest);
        }
    }

    /// <summary>壊れた履歴を暗黙初期化せず現在観測の欠損状態を返します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CorruptHistoryDoesNotSuppressCurrentObservationOrGetOverwritten()
    {
        using MediaTestFiles files = new();
        JsonFileRepository repository = new(Path.Combine(files.Root, "resource.json"));
        await repository.WriteLocalSnapshotAsync("peer:peer:resources", "history", NodeValue.FromObject("corrupt-history"));
        using ResourceService service = new(repository, "peer", new ResourceOptions());
        ResourceSample sample = await service.CaptureAsync();
        Assert.Same(sample, service.Latest);
        Assert.False(sample.HistoryStored);
        Assert.NotEmpty(sample.Metrics);
        Assert.Equal("corrupt-history", (await repository.ReadAsync("peer:peer:resources", "history"))!.Value!.GetString());
    }

    /// <summary>設定された保持時間を超える診断サンプルを残しません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task RetentionFiltersOldSamplesWhileKeepingNewOnes()
    {
        using MediaTestFiles files = new();
        JsonFileRepository repository = new(Path.Combine(files.Root, "resource.json"));
        ResourceSample[] samples = [new ResourceSample(Guid.NewGuid(), "peer", DateTimeOffset.UtcNow.AddHours(-2), [], true)];
        await repository.WriteLocalSnapshotAsync("peer:peer:resources", "history", NodeValue.FromJson(JsonSerializer.SerializeToElement(samples, ContractJson.Options)));
        using ResourceService service = new(repository, "peer", new ResourceOptions(10, 3, 1));
        Assert.Empty(await service.HistoryAsync());
        ResourceSample captured = await service.CaptureAsync();
        Assert.Equal(captured.Id, Assert.Single(await service.HistoryAsync()).Id);
    }

    /// <summary>ワークフローを作らなくてもHost収集が共通診断履歴を残します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task HostCollectionRunsWithoutWorkflow()
    {
        using MediaTestFiles files = new();
        using ResourceService service = new(new SqliteRepository(Path.Combine(files.Root, "resource.db")), "peer", new ResourceOptions(1, 5, 1));
        using CancellationTokenSource stop = new();
        Task run = service.RunAsync(stop.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = new TestTaskLifetime(run, stop).ConfigureAwait(true);

        // 採取を確認してから停止し、確認用の期限と自動停止を競合させません。
        using CancellationTokenSource wait = new(TimeSpan.FromSeconds(5));
        while ((await service.HistoryAsync(wait.Token)).Count < 2)
        {
            await Task.Delay(10, wait.Token).ConfigureAwait(true);
        }
        await stop.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run).ConfigureAwait(true);
        Assert.True(2 <= (await service.HistoryAsync()).Count);
    }

    /// <summary>sourceの予定終了を緊急取消へ取り違えず実測を渡します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ResourceSourceEmitsRealSamplesAndStopsNormally()
    {
        using MediaTestFiles files = new();
        using ResourceService service = new(new SqliteRepository(Path.Combine(files.Root, "resource.db")), "peer", new ResourceOptions());
        ISourceNode node = Assert.IsAssignableFrom<ISourceNode>(Assert.Single(ResourceNodeRegistration.Create(service), item => string.Equals(item.Descriptor.Type, "resource.watch", StringComparison.Ordinal)));
        List<NodeEmission> emissions = [];
        bool stopped = false;
        NodeExecutionContext initial = NodeTestContext.Create("""{"intervalMs":1}""");
        NodeSourceContext context = new(initial, () => initial, (emission, _) =>
        {
            emissions.Add(emission);
            stopped = emissions.Count == 2;
            return ValueTask.CompletedTask;
        }, () => stopped);
        NodeResult terminal = await node.RunAsync(context).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, terminal.Outcome);
        Assert.Equal(2, emissions.Count);
        Assert.All(emissions, emission => Assert.NotEmpty(emission.Result.Outputs["value"].Json.GetProperty("metrics").EnumerateArray()));
        Assert.Equal(2, (await service.HistoryAsync()).Count);
    }
}
