using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実UDPのバイト列／OSC Objectを解析し、別Coreへ型を保って渡します。</summary>
public sealed class OscParseProcessTests
{
    private static readonly string[] argumentTypes = ["i", "s", "b"];

    /// <summary>別Coreへ渡してからOSCパケットを再構築しても整数の精度を保持します。</summary>
    /// <returns>実UDP、解析、Peer間配送、再構築を通した型タグです。</returns>
    [Theory]
    [InlineData("i")]
    [InlineData("h")]
    public async Task IntegerPrecisionSurvivesPeerTransferAndPacketRebuildAsync(string tag)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
                && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null;
        }).ConfigureAwait(true);
        int inputPort = CorePeer.AvailablePort();
        WorkflowDefinition definition = new("osc-integer", "OSC integer precision", [
            new("receive", "osc.udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = inputPort, count = 1 }), Target: new(EExecutionScope.One, ["b"]), StartOnLoad: true),
            new("parse", OscPorts.ParseNodeType, JsonSerializer.SerializeToElement(new { argumentTypes = new[] { tag } })),
            new("target", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["a"])),
            new("packet", OscPorts.PacketNodeType, JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue> { ["address"] = NodeValue.FromObject("/again") }),
        ], [new("receive", "value", "parse", "value"), new("parse", "argument1", "target", "value"), new("target", "value", "packet", "input1")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => source.GetProperty("status").GetProperty("state").GetString() is "listening")).ConfigureAwait(true);
        using UdpClient sender = new();
        string hex = tag switch { "i" => "2F6E00002C69000000000001", _ => "2F6E00002C6800000000000000000001" };
        await sender.SendAsync(Convert.FromHexString(hex), new IPEndPoint(IPAddress.Loopback, inputPort)).ConfigureAwait(true);
        ExecutionReport? completed = null;
        await CorePeer.WaitAsync(async token =>
        {
            ExecutionHistoryPage history = await second.GetAsync<ExecutionHistoryPage>("executions.list", token).ConfigureAwait(true);
            completed = history.Entries.Select(entry => entry.Report).OfType<ExecutionReport>().FirstOrDefault(report => string.Equals(report.WorkflowId, definition.Id, StringComparison.Ordinal)
                && report.Nodes.Any(node => node.NodeId is "packet" && node.Outcome == EOutcome.Succeeded));
            return completed is not null;
        }).ConfigureAwait(true);
        TargetNodeResult transferred = Assert.Single(completed!.Nodes.Single(node => node.NodeId is "target").Targets);
        Assert.Equal("a", transferred.PeerId);
        NodeResult packet = Assert.Single(completed.Nodes.Single(node => node.NodeId is "packet").Targets).Result;
        JsonElement argument = Assert.Single(packet.Outputs["value"].Json.GetProperty("arguments").EnumerateArray());
        Assert.Equal(tag, argument.GetProperty("type").GetString());
        Assert.Equal(1, argument.GetProperty("value").GetInt64());
    }

    /// <summary>型違いの一件だけを拒否し、後続の正常パケットを処理し続けます。</summary>
    /// <returns>保存・共有・待受・解析・別Peer配送の確認です。</returns>
    [Theory]
    [InlineData("udp.receive", false)]
    [InlineData("osc.udp.receive", false)]
    [InlineData("udp.receive", true)]
    [InlineData("osc.udp.receive", true)]
    public async Task ReceivedPacketsAreParsedAndForwardedWithoutCoercionAsync(string receiverType, bool multiplePorts)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
                && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null;
        }).ConfigureAwait(true);
        int inputPort = CorePeer.AvailablePort();
        int otherPort = CorePeer.AvailablePort();
        while (otherPort == inputPort)
        {
            otherPort = CorePeer.AvailablePort();
        }
        JsonElement localPort = JsonSerializer.SerializeToElement(inputPort);
        if (multiplePorts)
        {
            localPort = JsonSerializer.SerializeToElement($"{inputPort}, {otherPort}-{otherPort}");
        }
        WorkflowDefinition definition = new("osc-parse", "OSC parse", [
            new("receive", receiverType, JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort, count = 3 }), Target: new(EExecutionScope.One, ["b"]), StartOnLoad: true),
            new("parse", OscPorts.ParseNodeType, JsonSerializer.SerializeToElement(new { argumentTypes })),
            new("target", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["a"])),
        ], [new("receive", "value", "parse", "value"), new("parse", "argument3", "target", "value")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => source.GetProperty("status").GetProperty("state").GetString() is "listening")).ConfigureAwait(true);
        using UdpClient sender = new();
        IPEndPoint endpoint = new(IPAddress.Loopback, inputPort);
        byte[] packet = Convert.FromHexString("2F706172736500002C697362000000000000002A637565000000000200FF0000");
        byte[] mismatch = packet.ToArray();
        mismatch[9] = (byte)'f';
        await sender.SendAsync(packet, endpoint).ConfigureAwait(true);
        if (multiplePorts)
        {
            endpoint = new(IPAddress.Loopback, otherPort);
        }
        await sender.SendAsync(mismatch, endpoint).ConfigureAwait(true);
        await sender.SendAsync(packet, endpoint).ConfigureAwait(true);
        ExecutionReport[] reports = [];
        await CorePeer.WaitAsync(async token =>
        {
            ExecutionHistoryPage history = await second.GetAsync<ExecutionHistoryPage>("executions.list", token).ConfigureAwait(true);
            reports = history.Entries.Select(entry => entry.Report).OfType<ExecutionReport>().Where(report => string.Equals(report.WorkflowId, definition.Id, StringComparison.Ordinal)).ToArray();
            return reports.Length == 4;
        }).ConfigureAwait(true);
        Assert.Equal(4, reports.Select(report => report.Id).Distinct().Count());
        Assert.Equal(1, reports.Count(report => report.Nodes.Single(node => node.NodeId is "parse").Outcome == EOutcome.Failed));
        ExecutionReport[] successful = reports.Where(report => report.Nodes.Single(node => node.NodeId is "target").Outcome == EOutcome.Succeeded).ToArray();
        Assert.Equal(2, successful.Length);
        foreach (ExecutionReport report in successful)
        {
            NodeResult parsed = Assert.Single(report.Nodes.Single(node => node.NodeId is "parse").Targets).Result;
            Assert.Equal("/parse", parsed.Outputs["address"].GetString());
            Assert.Equal(42, parsed.Outputs["argument1"].Json.GetInt32());
            Assert.Equal("cue", parsed.Outputs["argument2"].GetString());
            TargetNodeResult received = Assert.Single(report.Nodes.Single(node => node.NodeId is "target").Targets);
            Assert.Equal("a", received.PeerId);
            Assert.Equal(new byte[] { 0, 255 }, received.Result.Outputs["value"].Binary.ToArray());
        }
    }
}
