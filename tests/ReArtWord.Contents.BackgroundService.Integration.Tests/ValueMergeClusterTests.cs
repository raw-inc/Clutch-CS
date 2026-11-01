using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別プロセスのOSC待受から、一件に固定した値を共通の後続へ流します。</summary>
public sealed class ValueMergeClusterTests
{
    private static readonly string[] argumentTypes = ["i", "s"];

    /// <summary>複数PC・ポートの到着を合流し、遅延と項目別配線を経ても発生元と組を保ちます。</summary>
    /// <returns>実UDP・クラスタ配送・履歴に現れる三回の後続です。</returns>
    [Fact]
    public async Task OscArrivalsKeepTheirFieldsAndSourcePeerAcrossRemoteDelayAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int[] ports = CorePeer.AvailableUdpPorts(3);
        int port = ports[0], inputA = ports[1], inputB = ports[2];
        CorePeer first = new(cluster, "a", port, true, "host-a", 1000), second = new(cluster, "b", port, false, "host-b", 900);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
            && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null, timeout.Token).ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("shared-values", "Shared values", [
            Receiver("receive-a", "a", first.InterfaceId, inputA), Receiver("receive-b", "b", second.InterfaceId, inputB),
            new("merge", ValueMergePorts.NodeType, empty, Target: new(EExecutionScope.One, ["a"])),
            new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 300 }), Target: new(EExecutionScope.One, ["a"])),
            new("parse", "osc.parse", JsonSerializer.SerializeToElement(new { argumentTypes })),
            new("packet", "osc.packet", empty),
            new("sink", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 1 }), Target: new(EExecutionScope.One, SourcePeer: true)),
        ], [new("receive-a", "value", "merge", "input1"), new("receive-b", "value", "merge", "input2"),
            new("merge", "start", "delay", "trigger"), new("merge", "value", "delay", "value"), new("delay", "value", "parse", "value"),
            new("parse", "address", "packet", "address"), new("parse", "argument1", "packet", "input1"), new("parse", "argument2", "packet", "input2"),
            new("merge", "start", "sink", "trigger"), new("packet", "value", "sink", "value")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await ListeningAsync(first, "receive-a", timeout.Token).ConfigureAwait(true);
        await ListeningAsync(second, "receive-b", timeout.Token).ConfigureAwait(true);
        Assert.Empty(await SinkReportsAsync(first, second, timeout.Token).ConfigureAwait(true));
        await Task.WhenAll(SendAsync(inputA, "/from-a", 11, "alpha", timeout.Token), SendAsync(inputB, "/from-b", 22, "beta", timeout.Token)).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await SinkReportsAsync(first, second, token).ConfigureAwait(true)).Length == 2, timeout.Token).ConfigureAwait(true);
        await SendAsync(inputA, "/from-a", 11, "alpha", timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await SinkReportsAsync(first, second, token).ConfigureAwait(true)).Length == 3, timeout.Token).ConfigureAwait(true);
        ExecutionReport[] reports = await SinkReportsAsync(first, second, timeout.Token).ConfigureAwait(true);
        Assert.Equal(3, reports.Select(report => report.Id).Distinct().Count());
        Assert.All(reports, report => Assert.Equal(EOutcome.Succeeded, report.Outcome));
        foreach (ExecutionReport report in reports)
        {
            TargetNodeResult result = Assert.Single(report.Nodes.Single(node => node.NodeId is "sink").Targets);
            JsonElement value = result.Result.Outputs["value"].Json;
            (string Address, int Number, string Text) expected = result.PeerId switch
            {
                "a" => ("/from-a", 11, "alpha"),
                "b" => ("/from-b", 22, "beta"),
                _ => throw new InvalidOperationException(result.PeerId),
            };
            Assert.Equal(expected, (value.GetProperty("address").GetString(), value.GetProperty("arguments")[0].GetProperty("value").GetInt32(), value.GetProperty("arguments")[1].GetProperty("value").GetString()));
        }
        Assert.Equal(2, reports.Count(report => report.Nodes.Single(node => node.NodeId is "sink").Targets.Single().PeerId is "a"));
        Assert.Single(reports, report => report.Nodes.Single(node => node.NodeId is "sink").Targets.Single().PeerId is "b");
    }

    /// <summary>指定CoreにだけOSCの実待受を作ります。</summary>
    /// <returns>自動開始する受信定義です。</returns>
    private static NodeDefinition Receiver(string id, string peer, string nic, int port) => new(id, "osc.udp.receive",
        JsonSerializer.SerializeToElement(new { interfaceId = nic, localAddress = "127.0.0.1", localPort = port }), Target: new(EExecutionScope.One, [peer]), StartOnLoad: true);

    /// <summary>異なる型の項目を一つのOSCパケットとして送信します。</summary>
    /// <returns>実UDPへの送信完了です。</returns>
    private static async Task SendAsync(int port, string address, int number, string text, CancellationToken cancellationToken = default)
    {
        byte[] encodedNumber = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(encodedNumber, number);
        byte[] packet = [.. OscString(address), .. OscString(",is"), .. encodedNumber, .. OscString(text)];
        using UdpClient sender = new();
        await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>OSC文字列を終端付きの4バイト境界へ揃えます。</summary>
    /// <returns>実装側のcodecを使わない試験パケットの一部です。</returns>
    private static byte[] OscString(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + '\0');
        Array.Resize(ref bytes, (bytes.Length + 3) / 4 * 4);
        return bytes;
    }

    /// <summary>保存受付だけでなく、OSの待受開始を確認します。</summary>
    /// <returns>対象受信ノードのListeningです。</returns>
    private static Task ListeningAsync(CorePeer peer, string node, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
        (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray().Any(source => string.Equals(source.GetProperty("nodeId").GetString(), node, StringComparison.Ordinal)
            && source.GetProperty("status").GetProperty("state").GetString() is "listening"), cancellationToken);

    /// <summary>親と子の集計を混ぜず、実際に後続を配送した実行を取得します。</summary>
    /// <returns>両Peerの履歴から重複を除いた結果です。</returns>
    private static async Task<ExecutionReport[]> SinkReportsAsync(CorePeer first, CorePeer second, CancellationToken cancellationToken = default)
    {
        ExecutionHistoryPage a = await first.GetAsync<ExecutionHistoryPage>("executions.list", cancellationToken).ConfigureAwait(true);
        ExecutionHistoryPage b = await second.GetAsync<ExecutionHistoryPage>("executions.list", cancellationToken).ConfigureAwait(true);
        return a.Entries.Concat(b.Entries).Select(entry => entry.Report).OfType<ExecutionReport>().Where(report => report.WorkflowId is "shared-values"
            && report.Nodes.Single(node => node.NodeId is "sink").Outcome == EOutcome.Succeeded).DistinctBy(report => report.Id).ToArray();
    }
}
