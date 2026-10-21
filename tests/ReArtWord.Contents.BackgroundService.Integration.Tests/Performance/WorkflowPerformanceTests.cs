using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests.Performance;

/// <summary>実Core・実HTTP・実ファイルで、保存と繰返し並列実行の費用を測定します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class WorkflowPerformanceTests(ITestOutputHelper output)
{
    /// <summary>3ショットを別保存先へ3回実行し、全108ファイル・重複要求・使用量推移を確認します。</summary>
    /// <returns>通常/並列ForEachと大小の本文を同じ経路へ通した観測です。</returns>
    [Theory]
    [InlineData(1024, false)]
    [InlineData(262144, false)]
    [InlineData(262144, true)]
    public async Task ThreeConcurrentDownloadsPreserveFilesAndBoundResourcesAsync(int payloadBytes, bool parallel)
        => await DownloadsAsync(payloadBytes, parallel, 12, 2, 3, 3).ConfigureAwait(true);

    /// <summary>一WF500件・合計80並列で、外部APIを使わず実HTTPから別Coreへ保存します。</summary>
    /// <returns>通常の一回と3ショットの重なりにおける結果・メモリ・I/Oです。</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task FiveHundredFilesAtEightyConcurrencyPreserveResultsAsync(int shots)
        => await DownloadsAsync(65536, true, 500, 80, shots, 1).ConfigureAwait(true);

    /// <summary>指定した負荷条件だけを独立Coreへ渡し、実ファイルと要求回数を照合します。</summary>
    /// <returns>各周回の観測値と完全な出力確認です。</returns>
    private async Task DownloadsAsync(int payloadBytes, bool parallel, int itemsPerWorkflow, int concurrency, int shots, int rounds, CancellationToken cancellationToken = default)
    {
        PerformanceCluster cluster = new();
        LoopbackPayloadServer server = new(payloadBytes);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clusterLifetime = cluster.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable serverLifetime = server.ConfigureAwait(true);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(8));
        await cluster.StartAsync(limit.Token).ConfigureAwait(true);
        List<ProcessResourceSample[]> samples = [cluster.Capture()];
        List<double> elapsed = [];
        List<(string Path, string Key)> expected = [];
        for (int round = 0; round < rounds; round++)
        {
            List<(WorkflowDefinition Definition, WorkflowSaveResult Saved)> saved = [];
            for (int shot = 0; shot < shots; shot++)
            {
                string id = FormattableString.Invariant($"download-{round}-{shot}");
                string folder = Path.Combine(cluster.Peers[2].DirectoryPath, "content", id);
                Directory.CreateDirectory(folder);
                DownloadItem[] items = Enumerable.Range(0, itemsPerWorkflow).Select(index =>
                {
                    string key = FormattableString.Invariant($"/{round}/{shot}/{index}");
                    string path = Path.Combine(folder, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");
                    expected.Add((path, key));
                    return new DownloadItem(server.Url + key, path);
                }).ToArray();
                WorkflowDefinition definition = Download(id, items, cluster.Peers[1].InterfaceId, parallel, concurrency);
                WorkflowSaveResult result = await cluster.Peers[0].PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), limit.Token).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, result.Saved.Outcome);
                Assert.Equal(EOutcome.Succeeded, result.Shared.Outcome);
                saved.Add((definition, result));
            }
            Stopwatch watch = Stopwatch.StartNew();
            Task<WorkflowRunStatus[]> running = Task.WhenAll(saved.Select(item => cluster.RunAsync(item.Definition, item.Saved, TimeSpan.FromMinutes(6), limit.Token)));
            try
            {
                while (!running.IsCompleted)
                {
                    samples.Add(cluster.Capture());
                    await Task.Delay(200, limit.Token).ConfigureAwait(true);
                }
            }
            finally
            {
                if (!running.IsCompleted)
                {
                    await limit.CancelAsync().ConfigureAwait(true);
                }
                try
                {
                    await running.ConfigureAwait(true);
                }
                finally
                {
                    int presentFiles = expected.Count(item => File.Exists(item.Path));
                    output.WriteLine(FormattableString.Invariant($"Download progress: {server.Requests}/{expected.Count} HTTP requests, {presentFiles}/{expected.Count} files present."));
                    WriteReport(cluster, "parallel-download", new { payloadBytes, parallel, itemsPerWorkflow, concurrency, shots, rounds,
                        completedRounds = elapsed.Count, files = expected.Count, presentFiles, requests = server.Requests, elapsed, samples });
                }
            }
            elapsed.Add(watch.Elapsed.TotalMilliseconds);
            await Task.Delay(1000, limit.Token).ConfigureAwait(true);
            samples.Add(cluster.Capture());
        }
        WriteReport(cluster, "parallel-download", new { payloadBytes, parallel, itemsPerWorkflow, concurrency, shots, rounds,
            files = expected.Count, presentFiles = expected.Count(item => File.Exists(item.Path)), requests = server.Requests, elapsed, samples });
        Assert.Equal(expected.Count, server.Requests);
        foreach ((string path, string key) in expected)
        {
            Assert.Equal(server.Payload(key), await File.ReadAllBytesAsync(path, limit.Token).ConfigureAwait(true));
        }
        int writeBudgetFactor = itemsPerWorkflow <= 12 ? 32
                                                       : 128;
        CheckResources(samples, 1024L * 1024 * 1024 + payloadBytes * (long)itemsPerWorkflow * shots * 16,
            1024L * 1024 * 1024 + payloadBytes * (long)expected.Count * writeBudgetFactor);
    }

    /// <summary>通常と多ノード構成で位置変更を繰り返し保存し、保存版で別Coreの後続を実行します。</summary>
    /// <returns>各保存の時間と、保持量・I/O・実出力です。500msは機械的な合否条件にしません。</returns>
    [Theory]
    [InlineData(32)]
    [InlineData(256)]
    public async Task RepeatedPositionSavesStayBoundedAndExecutableAsync(int constants)
    {
        PerformanceCluster cluster = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clusterLifetime = cluster.ConfigureAwait(true);
        using CancellationTokenSource limit = new(TimeSpan.FromMinutes(3));
        await cluster.StartAsync(limit.Token).ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        string path = Path.Combine(cluster.Peers[1].DirectoryPath, "content", "saved.txt");
        List<NodeDefinition> nodes = [new("start", "control.button", empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("expected") }, new(EExecutionScope.One, ["a"])),
            new("write", "file.upsert", JsonSerializer.SerializeToElement(new { path }), Target: new(EExecutionScope.One, ["b"]))];
        nodes.AddRange(Enumerable.Range(0, constants).Select(index => new NodeDefinition("constant-" + index, "value.constant", JsonSerializer.SerializeToElement(new { text = "42" }))));
        WorkflowDefinition definition = new("save-load", "Save and execute", nodes, [new("start", "value", "write", "value")]);
        List<ProcessResourceSample[]> samples = [cluster.Capture()];
        List<double> milliseconds = [];
        string? revision = null;
        for (int index = 0; index < 8; index++)
        {
            nodes[0] = nodes[0] with { Position = new(index, 0) };
            Stopwatch watch = Stopwatch.StartNew();
            WorkflowSaveResult saved = await cluster.Peers[0].PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, revision), limit.Token).ConfigureAwait(true);
            milliseconds.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
            Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
            revision = saved.Revision;
            WorkflowRunStatus result = await cluster.Peers[0].PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.NewGuid(), definition.Id, revision, "start"), limit.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token =>
            {
                result = await cluster.Peers[0].GetAsync<WorkflowRunStatus>("runs.read", result.OperationId.ToString("D"), token).ConfigureAwait(true);
                return !result.Running;
            }, limit.Token).ConfigureAwait(true);
            Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
            Assert.Equal("expected", await File.ReadAllTextAsync(path, limit.Token).ConfigureAwait(true));
            samples.Add(cluster.Capture());
        }
        WriteReport(cluster, "save", new { constants, milliseconds, samples });
        CheckResources(samples, 1024L * 1024 * 1024, 512L * 1024 * 1024);
    }

    /// <summary>要求情報だけを反復へ渡し、Binaryは別Coreの保存ノードまで実転送します。</summary>
    /// <returns>外部宛先を含まない検証用ワークフローです。</returns>
    private static WorkflowDefinition Download(string id, DownloadItem[] items, string nic, bool parallel, int concurrency)
    {
        if (32 < concurrency)
        {
            List<NodeDefinition> nodes = [];
            List<WireDefinition> wires = [];
            int offset = 0;
            for (int remaining = concurrency; 0 < remaining; remaining -= 32)
            {
                int width = Math.Min(32, remaining);
                int count = remaining <= 32 ? items.Length - offset
                                            : items.Length * width / concurrency;
                WorkflowDefinition part = Download(id, items.Skip(offset).Take(count).ToArray(), nic, parallel, width);
                string prefix = offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-";
                nodes.AddRange(part.Nodes.Select(node => node with { Id = prefix + node.Id }));
                wires.AddRange(part.Wires.Select(wire => wire with { FromNode = prefix + wire.FromNode, ToNode = prefix + wire.ToNode }));
                offset += count;
            }
            return new(id, id, nodes, wires);
        }
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        string type = parallel ? "control.parallelForEach"
                               : "control.forEach";
        JsonElement iteration = parallel ? JsonSerializer.SerializeToElement(new { maxConcurrency = concurrency })
                                         : empty;
        return new(id, id,
        [
            new("start", type, iteration, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(items) }, new(EExecutionScope.One, ["a"])),
            new("get", "http.request", JsonSerializer.SerializeToElement(new { interfaceId = nic, localAddress = "127.0.0.1", method = "GET", authentication = "none", url = items[0].Url, responseBodyFormat = "binary" }), Target: new(EExecutionScope.One, ["b"])),
            new("write", "file.create", JsonSerializer.SerializeToElement(new { path = items[0].Path }), Target: new(EExecutionScope.One, ["c"])),
            new("end", "control.forEach.end", empty, Target: new(EExecutionScope.One, ["a"])),
        ],
        [
            new("start", "item", "get", "$settings.url", Path: "/Url"),
            new("start", "item", "write", "$settings.path", Path: "/Path"),
            new("get", "body", "write", "value"),
            new("start", "loop", "end", "loop"),
            new("write", "value", "end", "value"),
        ]);
    }

    /// <summary>少量fixtureでGB単位の膨張を見逃さず、機械間の小さい速度差とは分けて判定します。</summary>
    private static void CheckResources(List<ProcessResourceSample[]> samples, long maximumPrivate, long maximumWrite)
    {
        Assert.All(samples.SelectMany(sample => sample), sample => Assert.InRange(sample.PrivateBytes, 0, maximumPrivate));
        if (samples[0].All(sample => sample.WriteBytes.HasValue) && samples[^1].All(sample => sample.WriteBytes.HasValue))
        {
            long bytes = samples[^1].Sum(sample => sample.WriteBytes!.Value) - samples[0].Sum(sample => sample.WriteBytes!.Value);
            Assert.InRange(bytes, 0, maximumWrite);
        }
    }

    /// <summary>固有fixtureの下へ実測を保存し、複数回の試験結果を上書きしません。</summary>
    private void WriteReport(PerformanceCluster cluster, string scenario, object measurement)
    {
        string path = Path.Combine(cluster.Peers[0].DirectoryPath, scenario + "-measurement.json");
        string json = JsonSerializer.Serialize(measurement);
        File.WriteAllText(path, json);
        output.WriteLine(path);
    }

    /// <summary>一つの反復だけに属するHTTP宛先と出力先です。</summary>
    private sealed record DownloadItem(string Url, string Path);
}
