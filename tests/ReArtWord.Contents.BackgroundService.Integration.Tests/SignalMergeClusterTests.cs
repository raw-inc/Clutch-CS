using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別Coreの受信・状態保存・後続配送を実通信でつなぎます。</summary>
public sealed class SignalMergeClusterTests
{
    /// <summary>別々の入力で値を保持してから合流し、未更新の値ノードを再実行しません。</summary>
    /// <returns>クラスタ共通状態を用いる二巡の実行です。</returns>
    [Fact]
    public async Task SeparatePeersShareSignalsAndHeldValuesAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int[] ports = CorePeer.AvailableUdpPorts(3);
        int port = ports[0], inputA = ports[1], inputB = ports[2];
        CorePeer first = new(cluster, "a", port, true, "host-a", 1000), second = new(cluster, "b", port, false, "host-b", 900);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
            && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null).ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("shared-signals", "Shared signals", [
            Receiver("receive-a", "a", first.InterfaceId, inputA), Receiver("receive-b", "b", second.InterfaceId, inputB),
            new("held-a", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 1 }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(10) }, Target: new(EExecutionScope.One, SourcePeer: true)),
            new("held-b", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 1 }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(20) }, Target: new(EExecutionScope.One, SourcePeer: true)),
            new("merge", "control.signal.all", empty, Target: new(EExecutionScope.One, SourcePeer: true)),
            new("sink", "math.add", empty, Target: new(EExecutionScope.One, ["a"])),
        ], [new("receive-a", "start", "held-a", "trigger"), new("receive-b", "start", "held-b", "trigger"),
            new("held-a", CompletionPorts.Signal, "merge", "input1"), new("held-b", CompletionPorts.Signal, "merge", "input2"),
            new("merge", "start", "sink", "trigger"), new("held-a", "value", "sink", "left"), new("held-b", "value", "sink", "right")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await ListeningAsync(first, "receive-a").ConfigureAwait(true);
        await ListeningAsync(second, "receive-b").ConfigureAwait(true);
        await SendAsync(inputA).ConfigureAwait(true);
        await SourceCountAsync(first, "receive-a", 1).ConfigureAwait(true);
        Assert.Empty(await SinkReportsAsync(first, second).ConfigureAwait(true));
        await SendAsync(inputB).ConfigureAwait(true);
        await SourceCountAsync(second, "receive-b", 1).ConfigureAwait(true);
        ExecutionReport[] reports = await SinkReportsAsync(first, second).ConfigureAwait(true);
        Assert.Single(reports);
        Assert.Equal(30, Assert.Single(reports[0].Nodes.Single(node => node.NodeId is "sink").Targets).Result.Outputs["value"].Json.GetInt32());
        Assert.Equal(EOutcome.NotExecuted, reports[0].Nodes.Single(node => node.NodeId is "held-a").Outcome);
        Assert.Equal(EOutcome.NotExecuted, reports[0].Nodes.Single(node => node.NodeId is "held-b").Outcome);
        await SendAsync(inputA).ConfigureAwait(true);
        await SourceCountAsync(first, "receive-a", 2).ConfigureAwait(true);
        await SendAsync(inputA).ConfigureAwait(true);
        await SourceCountAsync(first, "receive-a", 3).ConfigureAwait(true);
        Assert.Single(await SinkReportsAsync(first, second).ConfigureAwait(true));
        await SendAsync(inputB).ConfigureAwait(true);
        await SourceCountAsync(second, "receive-b", 2).ConfigureAwait(true);
        Assert.Equal(2, (await SinkReportsAsync(first, second).ConfigureAwait(true)).Length);
    }

    /// <summary>指定した実CoreだけにOSC待受を配置します。</summary>
    /// <returns>空引数の受信ノードです。</returns>
    private static NodeDefinition Receiver(string id, string peer, string nic, int port) => new(id, "osc.udp.receive",
        JsonSerializer.SerializeToElement(new { interfaceId = nic, localAddress = "127.0.0.1", localPort = port }), Target: new(EExecutionScope.One, [peer]), StartOnLoad: true);

    /// <summary>実UDPで一つの開始事象を送ります。</summary>
    /// <returns>送信済みの確認です。</returns>
    private static async Task SendAsync(int port, CancellationToken cancellationToken = default)
    {
        using UdpClient sender = new();
        await sender.SendAsync(Encoding.ASCII.GetBytes("/cue\0\0\0\0,\0\0\0"), new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>保存受付ではなく実際の待受を確認します。</summary>
    /// <returns>指定ノードの待受開始です。</returns>
    private static Task ListeningAsync(CorePeer peer, string node, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
        (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray().Any(source => string.Equals(source.GetProperty("nodeId").GetString(), node, StringComparison.Ordinal)
            && source.GetProperty("status").GetProperty("state").GetString() is "listening"), cancellationToken);

    /// <summary>子実行を待ち終えた元入力の報告数を確認します。</summary>
    /// <returns>指定件数の完了です。</returns>
    private static Task SourceCountAsync(CorePeer peer, string node, int count, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
        (await peer.GetAsync<ExecutionHistoryPage>("executions.list", token).ConfigureAwait(true)).Entries.Select(entry => entry.Report).OfType<ExecutionReport>()
            .Count(report => report.WorkflowId is "shared-signals" && report.Nodes.Single(item => string.Equals(item.NodeId, node, StringComparison.Ordinal)).Targets
                .Any(target => target.Result.Outputs.ContainsKey("start"))) == count, cancellationToken);

    /// <summary>元入力と子実行を二重計上せず、実際に配送した後続だけを数えます。</summary>
    /// <returns>合流から発火した一件の結果です。</returns>
    private static async Task<ExecutionReport[]> SinkReportsAsync(CorePeer first, CorePeer second, CancellationToken cancellationToken = default)
    {
        ExecutionHistoryPage a = await first.GetAsync<ExecutionHistoryPage>("executions.list", cancellationToken).ConfigureAwait(true);
        ExecutionHistoryPage b = await second.GetAsync<ExecutionHistoryPage>("executions.list", cancellationToken).ConfigureAwait(true);
        return a.Entries.Concat(b.Entries).Select(entry => entry.Report).OfType<ExecutionReport>().Where(report => report.WorkflowId is "shared-signals"
            && report.Nodes.Single(node => node.NodeId is "sink").Outcome == EOutcome.Succeeded).DistinctBy(report => report.Id).ToArray();
    }
}
