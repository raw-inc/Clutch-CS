using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>連続受信・開始信号の共有値・遠隔並列反復を実Core同士で組み合わせます。</summary>
public sealed class RetainedValueBurstTests
{
    /// <summary>短時間の入力が保存待ちに滞留せず、全要素の結果を一回ずつ返します。</summary>
    /// <returns>全入力の完了と、実行・待機が残らないことです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedRetentionCompletesAcrossConcurrentSourceInputs(bool historicalState)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, priority: 100);
        CorePeer second = new(cluster, "b", port, false, priority: 50);
        CorePeer third = new(cluster, "c", port, false, priority: 25);
        CorePeer fourth = new(cluster, "d", port, false, priority: 20);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable thirdLifetime = third.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable fourthLifetime = fourth.ConfigureAwait(true);
        await first.StartAsync();
        if (historicalState)
        {
            IDurableRepository historyStore = await RepositoryFactory.CreateAsync(Path.Combine(first.DirectoryPath, "state"), "a", new PersistenceOptions());
            for (int index = 0; index < 256; index++)
            {
                Assert.Equal(EOutcome.Succeeded, (await historyStore.ApplyBatchAsync(Guid.NewGuid(),
                    [new("workflow:history", index.ToString(System.Globalization.CultureInfo.InvariantCulture), null, NodeValue.FromObject(index))])).Outcome);
            }
        }
        await second.StartAsync();
        int expectedPeers = 2;
        if (historicalState)
        {
            await third.StartAsync();
            await fourth.StartAsync();
            expectedPeers = 4;
        }
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == expectedPeers);
        int inputPort = CorePeer.AvailablePort();
        WorkflowDefinition definition = Definition(first.InterfaceId, inputPort);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null));
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => source.GetProperty("status").GetProperty("state").GetString() is "listening"));
        using UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] packet = "/burst\0\0,\0\0\0"u8.ToArray();
        for (int index = 0; index < 20; index++)
        {
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, inputPort));
            await Task.Delay(50);
        }
        ExecutionHistoryPage history = new([], null);
        await CorePeer.WaitAsync(async token =>
        {
            history = await first.GetAsync<ExecutionHistoryPage>("executions.list", token).ConfigureAwait(true);
            return history.Entries.Count == 20;
        }, TimeSpan.FromSeconds(90));
        Assert.All(history.Entries, entry =>
        {
            Assert.Equal(EOutcome.Succeeded, entry.Report?.Outcome);
            NodeExecutionReport end = Assert.Single(entry.Report!.Nodes, node => node.NodeId is "end");
            Assert.Equal(Enumerable.Range(1, 12).Select(value => value * 10), Assert.Single(end.Targets).Result.Outputs["value"].Json.EnumerateArray().Select(value => value.GetInt32()));
        });
        JsonElement status = await first.GetAsync<JsonElement>("status");
        Assert.Equal(0, status.GetProperty("sourceQueue").GetProperty("running").GetInt32());
        Assert.Equal(0, status.GetProperty("sourceQueue").GetProperty("waiting").GetInt32());
        Assert.Empty(status.GetProperty("runningNodes").EnumerateArray());
    }

    /// <summary>実機の宛先・保存済みWFへ依存しない最小の分散配線です。</summary>
    /// <returns>前後の開始信号だけが値保持を更新する有限処理です。</returns>
    private static WorkflowDefinition Definition(string interfaceId, int inputPort)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        TargetDefinition local = new(EExecutionScope.One, ["a"]);
        TargetDefinition remote = new(EExecutionScope.One, ["b"]);
        NodeDefinition[] nodes =
        [
            new("receive", "osc.udp.receive", JsonSerializer.SerializeToElement(new { interfaceId, localAddress = "127.0.0.1", localPort = inputPort }), Target: local, StartOnLoad: true),
            new("before", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 1 }), Target: local),
            new("start", "control.parallelForEach", JsonSerializer.SerializeToElement(new { maxConcurrency = 12 }),
                new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(Enumerable.Range(1, 12).ToArray()) }, local),
            new("multiply", "math.multiply", empty, new Dictionary<string, NodeValue> { ["right"] = NodeValue.FromObject(10) }, remote),
            new("end", "control.forEach.end", empty, Target: local),
            new("after", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 1 }), Target: local),
        ];
        WireDefinition[] wires =
        [
            new("receive", "start", "before", "trigger"),
            new("before", "$completed", "start", "trigger"),
            new("start", "item", "multiply", "left"),
            new("start", "loop", "end", "loop"),
            new("multiply", "value", "end", "value"),
            new("end", "$completed", "after", "trigger"),
        ];
        return new("retained-burst", "retained burst", nodes, wires, Enabled: true);
    }
}
