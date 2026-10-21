using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>継続するOSC受信から別Peerの実行時接続先へ、保存・共有・実通信を通して配送します。</summary>
public sealed class RuntimeDestinationProcessTests
{
    /// <summary>自動開始OFFからONへ反映し、同じ待受で受信した値から別Peer上の送信先を決めます。</summary>
    /// <returns>三回の実通信と、再帰的な再開始をせず待受が継続する確認です。</returns>
    [Fact]
    public async Task ContinuousOscInputSuppliesDynamicDestinationOnAnotherPeer()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, priority: 10);
        CorePeer second = new(cluster, "b", port, false, priority: 1);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2).ConfigureAwait(true);
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient sender = new();
        int destinationPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        int inputPort = CorePeer.AvailablePort();
        int sourcePort = CorePeer.AvailablePort();
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("dynamic-osc-destination", "Continuous OSC routing", [
            new("receive", "osc.udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = first.InterfaceId, localAddress = "127.0.0.1", localPort = inputPort, count = 0 }), Target: new(EExecutionScope.One, ["a"])),
            new("heartbeat", "control.condition", JsonSerializer.SerializeToElement(new { operation = "notEqual", expected = "/all/ping" })),
            new("regex", "text.regex", JsonSerializer.SerializeToElement(new { mode = "first" }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["pattern"] = NodeValue.FromObject("^/host/(?<host>1)/") }),
            new("concat", "data.concat", empty, new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input1"] = NodeValue.FromObject("127.0.0.") }),
            new("port-number", "data.convert", JsonSerializer.SerializeToElement(new { targetType = "integer" })),
            new("add-port", "math.add", empty, new Dictionary<string, NodeValue> { ["left"] = NodeValue.FromObject(sourcePort - 1) }),
            new("port-comment", "value.comment", empty, new Dictionary<string, NodeValue> { ["displayName"] = NodeValue.FromObject("送信ピアで使うポート") }),
            new("send", "osc.udp.send", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", remotePort = destinationPort }), Target: new(EExecutionScope.One, ["b"])),
        ], [new("receive", "address", "heartbeat", "comparison"), new("receive", "value", "heartbeat", "value"),
            new("heartbeat", "comparison", "regex", "value", When: "true"), new("regex", "value/host", "concat", "input2"),
            new("regex", "value/host", "port-number", "value"), new("port-number", "value", "add-port", "right"),
            new("add-port", "value", "port-comment", "value"), new("port-comment", "value", "send", SettingPorts.Name("localPort")),
            new("concat", "value", "send", SettingPorts.Name("remoteAddress")), new("heartbeat", "value", "send", "value", When: "true")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved, ContractJson.Options));
        Assert.Empty((await first.GetAsync<JsonElement>("status").ConfigureAwait(true)).GetProperty("sources").EnumerateArray());
        definition = definition with { Nodes = [definition.Nodes[0] with { StartOnLoad = true }, .. definition.Nodes.Skip(1)] };
        saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, saved.Revision)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        Guid session = Guid.Empty;
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            foreach (JsonElement source in status.GetProperty("sources").EnumerateArray())
            {
                bool listening = string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "listening", StringComparison.Ordinal);
                if (listening)
                {
                    session = source.GetProperty("id").GetGuid();
                    return true;
                }
            }
            return false;
        }).ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        for (int index = 0; index < 3; index++)
        {
            byte[] legacyPing = Convert.FromHexString("2F616C6C2F70696E67000000");
            await sender.SendAsync(legacyPing, new IPEndPoint(IPAddress.Loopback, inputPort), deadline.Token).ConfigureAwait(true);
            await CheckIgnoredPingsAsync(first, definition.Id, (index * 2) + 1, deadline.Token).ConfigureAwait(true);
            Assert.Equal(0, receiver.Available);
            byte[] packet = Encoding.ASCII.GetBytes($"/host/1/{index}\0\0\0,\0\0\0");
            byte[] incoming = packet;
            if (index == 1)
            {
                incoming = packet[..^4];
            }
            await sender.SendAsync(incoming, new IPEndPoint(IPAddress.Loopback, inputPort), deadline.Token).ConfigureAwait(true);
            UdpReceiveResult received = await receiver.ReceiveAsync(deadline.Token).ConfigureAwait(true);
            Assert.Equal(packet, received.Buffer);
            Assert.Equal(sourcePort, received.RemoteEndPoint.Port);
            byte[] standardPing = Convert.FromHexString("2F616C6C2F70696E670000002C000000");
            await sender.SendAsync(standardPing, new IPEndPoint(IPAddress.Loopback, inputPort), deadline.Token).ConfigureAwait(true);
            await CheckIgnoredPingsAsync(first, definition.Id, (index * 2) + 2, deadline.Token).ConfigureAwait(true);
            Assert.Equal(0, receiver.Available);
        }
        JsonElement final = await first.GetAsync<JsonElement>("status", deadline.Token).ConfigureAwait(true);
        JsonElement listener = Assert.Single(final.GetProperty("sources").EnumerateArray());
        Assert.Equal(session, listener.GetProperty("id").GetGuid());
        Assert.Equal("listening", listener.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, listener.GetProperty("result").ValueKind);
        JsonElement remote = await second.GetAsync<JsonElement>("status", deadline.Token).ConfigureAwait(true);
        Assert.Contains(remote.GetProperty("nodeValues").EnumerateArray(), value => string.Equals(value.GetProperty("nodeId").GetString(), "send", StringComparison.Ordinal));
    }

    /// <summary>死活入力の処理完了を待ち、直前の制御先を再利用して送信していないことを確認します。</summary>
    /// <returns>通常入力の前後どちらでも正常破棄された実行結果です。</returns>
    private static Task CheckIgnoredPingsAsync(CorePeer peer, string workflowId, int count, CancellationToken cancellationToken = default)
        => CorePeer.WaitAsync(async token =>
        {
            ExecutionHistoryPage history = await peer.GetAsync<ExecutionHistoryPage>("executions.list", new ExecutionHistoryQuery(), token).ConfigureAwait(true);
            ExecutionReport[] reports = history.Entries.Where(entry => entry.Report is not null).Select(entry => entry.Report!)
                .Where(report => string.Equals(report.WorkflowId, workflowId, StringComparison.Ordinal)
                    && report.Nodes.Where(node => node.NodeId is "receive").SelectMany(node => node.Targets)
                        .Any(target => target.Result.Outputs.TryGetValue("address", out NodeValue? address) && string.Equals(address.GetString(), "/all/ping", StringComparison.Ordinal))).ToArray();
            if (reports.Length < count)
            {
                return false;
            }
            foreach (ExecutionReport report in reports)
            {
                Assert.Equal(EOutcome.Succeeded, report.Outcome);
                Assert.Equal(EOutcome.NotExecuted, Assert.Single(report.Nodes, node => node.NodeId is "send").Outcome);
            }
            return true;
        }, cancellationToken);
}
