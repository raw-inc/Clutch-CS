using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>隔離した実Core間の保存・実行時間を、同じ操作条件で継続計測します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class WorkflowLatencyTests(ITestOutputHelper output)
{
    /// <summary>ノード設定変更を伴う保存と別Peerでのファイル出力について、冷間・反復の時間を記録します。</summary>
    /// <returns>確定保存、出力の内容、各操作の経過時間です。性能の固定閾値は実機差と分離します。</returns>
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public async Task MeasuresConfirmedSaveAndExecutionAcrossProcessesAsync(int peerCount)
    {
        string cluster = Guid.NewGuid().ToString("N");
        PeerSet peers = new(peerCount, cluster, CorePeer.AvailablePort(), Environment.GetEnvironmentVariable("CONTENTS_CORE_PROBE_EXECUTABLE"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peers.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(150));
        foreach (CorePeer peer in peers.Items)
        {
            await peer.StartAsync(timeout.Token).ConfigureAwait(true);
        }
        CorePeer first = peers.Items[0], last = peers.Items[^1];
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == peerCount, timeout.Token).ConfigureAwait(true);
        List<string> builds = [];
        foreach (CorePeer peer in peers.Items)
        {
            builds.Add((await peer.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true)).GetProperty("buildVersion").GetString()!);
        }
        string build = Assert.Single(builds.Distinct(StringComparer.Ordinal));
        string input = Path.Combine(first.DirectoryPath, "content", "input.txt"), destination = Path.Combine(last.DirectoryPath, "content", "output.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        List<NodeDefinition> nodes = [new("read", "file.read", JsonSerializer.SerializeToElement(new { path = input }), Target: new(EExecutionScope.One, ["p0"]))];
        List<WireDefinition> wires = [];
        string previous = "read";
        for (int index = 0; index < 96; index++)
        {
            string id = "relay-" + index;
            nodes.Add(new(id, "control.pass", empty, Position: new(index * 100, 100)));
            wires.Add(new(previous, "value", id, "value"));
            previous = id;
        }
        nodes.Add(new("write", "file.upsert", JsonSerializer.SerializeToElement(new { path = destination }), Target: new(EExecutionScope.One, ["p" + (peerCount - 1)])));
        wires.Add(new(previous, "value", "write", "value"));
        WorkflowDefinition definition = new("latency-probe", "Latency probe", nodes, wires);
        string? revision = null;
        List<Sample> samples = [];
        for (int iteration = 0; iteration < 3; iteration++)
        {
            input = Path.Combine(first.DirectoryPath, "content", "input-" + iteration + ".txt");
            NodeDefinition reader = definition.Nodes[0] with { Settings = JsonSerializer.SerializeToElement(new { path = input }) };
            definition = definition with { Name = "Latency probe " + iteration, Nodes = [reader, .. definition.Nodes.Skip(1)] };
            string expected = "iteration:" + iteration + ":" + new string('x', 4096);
            await File.WriteAllTextAsync(input, expected, timeout.Token).ConfigureAwait(true);
            Stopwatch watch = Stopwatch.StartNew();
            WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, revision), timeout.Token).ConfigureAwait(true);
            double saveMilliseconds = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
            Assert.Equal(EOutcome.Succeeded, saved.Snapshot.Outcome);
            Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
            if (iteration != 0)
            {
                Assert.Equal(EOutcome.Succeeded, saved.BeforeSnapshot?.Outcome);
            }
            revision = saved.Revision;
            Guid operation = Guid.NewGuid();
            watch.Restart();
            WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, definition.Id, revision), timeout.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token => !(run = await first.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(true)).Running, timeout.Token).ConfigureAwait(true);
            double runMilliseconds = watch.Elapsed.TotalMilliseconds;
            Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
            Assert.Equal(expected, await File.ReadAllTextAsync(destination, timeout.Token).ConfigureAwait(true));
            Sample sample = new(iteration, peerCount, nodes.Count, saveMilliseconds, runMilliseconds, saved.BeforeSnapshot?.Outcome == EOutcome.Succeeded, build);
            samples.Add(sample);
            output.WriteLine(JsonSerializer.Serialize(sample));
        }
        await File.WriteAllTextAsync(Path.Combine(first.DirectoryPath, "latency.json"), JsonSerializer.Serialize(samples), timeout.Token).ConfigureAwait(true);
    }

    /// <summary>同じ条件を再測定できる、成功した操作の実時間です。</summary>
    private sealed record Sample(int Iteration, int Peers, int Nodes, double SaveMilliseconds, double RunMilliseconds, bool BeforeSnapshotConfirmed, string CoreBuild);

    /// <summary>試験が作った実Coreだけを、失敗時にも終了します。</summary>
    private sealed class PeerSet(int count, string cluster, int port, string? executable) : IAsyncDisposable
    {
        internal CorePeer[] Items
        {
            get;
        } = Enumerable.Range(0, count).Select(index => new CorePeer(cluster, "p" + index, port, index == 0, "host-" + index, count - index, executable)).ToArray();

        /// <summary>通信を終了し、各試験プロセスの結果を回収します。</summary>
        /// <returns>全Coreの終了です。</returns>
        public async ValueTask DisposeAsync()
        {
            foreach (CorePeer peer in Items)
            {
                await ((IAsyncDisposable)peer).DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
