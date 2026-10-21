using System.Net;
using System.Net.Sockets;
using System.Text;
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

/// <summary>実OSC受信を開始信号にして、別Coreの処理を入力一件ずつ起動します。</summary>
public sealed class SourceStartSignalTests
{
    /// <summary>正常パケットだけが後続を起動し、不正パケットと待受終了では追加実行しません。</summary>
    /// <returns>実Core・UDP・別Peer配送の確認です。</returns>
    [Fact]
    public async Task OscInputsStartRemoteActionsButFailureAndCompletionDoNot()
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
        WorkflowDefinition definition = new("source-start", "Source start", [
            new("receive", "osc.udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = inputPort, count = 3 }), Target: new(EExecutionScope.One, ["b"]), StartOnLoad: true),
            new("target", "control.pass", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("received") }, Target: new(EExecutionScope.One, ["a"])),
        ], [new("receive", "start", "target", "trigger", "*")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => source.GetProperty("status").GetProperty("state").GetString() is "listening")).ConfigureAwait(true);
        using UdpClient sender = new();
        IPEndPoint endpoint = new(IPAddress.Loopback, inputPort);
        byte[] packet = Encoding.ASCII.GetBytes("/cue\0\0\0\0,\0\0\0");
        await sender.SendAsync(packet, endpoint).ConfigureAwait(true);
        await sender.SendAsync(new byte[] { 1, 2, 3 }, endpoint).ConfigureAwait(true);
        await sender.SendAsync(packet, endpoint).ConfigureAwait(true);
        ExecutionReport[] reports = [];
        await CorePeer.WaitAsync(async token =>
        {
            ExecutionHistoryPage history = await second.GetAsync<ExecutionHistoryPage>("executions.list", token).ConfigureAwait(true);
            reports = history.Entries.Select(entry => entry.Report).OfType<ExecutionReport>().Where(report => report.WorkflowId is "source-start").ToArray();
            return reports.Length == 4;
        }).ConfigureAwait(true);
        Assert.Equal(4, reports.Select(report => report.Id).Distinct().Count());
        Assert.Equal(2, reports.Count(report => report.Nodes.Single(node => node.NodeId is "target").Outcome == EOutcome.Succeeded));
        foreach (ExecutionReport report in reports)
        {
            NodeResult received = Assert.Single(report.Nodes.Single(node => node.NodeId is "receive").Targets).Result;
            NodeExecutionReport target = report.Nodes.Single(node => node.NodeId is "target");
            bool signaled = received.Outputs.ContainsKey("start");
            Assert.Equal(signaled ? EOutcome.Succeeded
                                  : EOutcome.NotExecuted, target.Outcome);
            if (signaled)
            {
                TargetNodeResult executed = Assert.Single(target.Targets);
                Assert.Equal("a", executed.PeerId);
                Assert.Equal("received", executed.Result.Outputs["value"].GetString());
                Assert.True(received.Outputs["start"].Json.GetBoolean());
                Assert.Equal("/cue", received.Outputs["address"].GetString());
            }
        }
    }
}
