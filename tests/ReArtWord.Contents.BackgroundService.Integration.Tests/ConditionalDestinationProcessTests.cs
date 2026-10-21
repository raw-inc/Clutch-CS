using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>二つのCoreで分岐と送信を分け、非選択時には実UDPも送信されないことを確認します。</summary>
public sealed class ConditionalDestinationProcessTests
{
    private static readonly bool[] selections = [false, true, false];

    /// <summary>保存版を変えず、要求ごとに選択された宛先だけへ一回送信します。</summary>
    /// <returns>実管理通信、実UDP、非成功ログの有無を含む確認です。</returns>
    [Fact]
    public async Task RemoteGateSkipsUnselectedUdpDestinationAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int destination = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("conditional-send", "Conditional destination", [
            new("button", "control.button", empty, Target: new(EExecutionScope.One, ["a"])),
            new("gate", "control.ifStart", empty, Target: new(EExecutionScope.One, ["b"])),
            new("address", "value.constant", JsonSerializer.SerializeToElement(new { text = "127.0.0.1" })),
            new("converted", "data.convert", JsonSerializer.SerializeToElement(new { targetType = "string" })),
            new("send", "osc.udp.send", JsonSerializer.SerializeToElement(new
            {
                interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = 0, remotePort = destination, timeoutMs = 2000,
            }), Target: new(EExecutionScope.One, ["b"]))],
            [new("button", "value/selected", "gate", "condition"), new("gate", "start", "address", "trigger"),
                new("address", "value", "converted", "value"), new("converted", "value", "send", "$settings.remoteAddress"),
                new("button", "value/packet", "send", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        foreach (bool selected in selections)
        {
            JsonElement packet = JsonSerializer.SerializeToElement(new { address = "/branch-probe", arguments = Array.Empty<object>() });
            WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision, "button",
                new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(new { selected, packet }) });
            _ = await first.PostAsync<WorkflowRunStatus>("runs.start", request, timeout.Token).ConfigureAwait(true);
            WorkflowRunStatus? run = null;
            await CorePeer.WaitAsync(async token =>
            {
                run = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId, token).ConfigureAwait(true);
                return !run.Running;
            }, timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, run!.Outcome);
            NodeExecutionReport sender = Assert.Single(run.Report!.Nodes, node => node.NodeId is "send");
            Assert.Equal("b", Assert.Single(sender.Targets).PeerId);
            if (selected)
            {
                UdpReceiveResult received = await receiver.ReceiveAsync(timeout.Token).ConfigureAwait(true);
                Assert.Equal(Encoding.UTF8.GetBytes("/branch-probe\0\0\0,\0\0\0"), received.Buffer);
                Assert.Equal(EOutcome.Succeeded, sender.Outcome);
            }
            else
            {
                Assert.Equal(ExecutionResultReasons.BranchNotSelected, Assert.Single(sender.Targets).Result.ReasonCode);
            }
            Assert.Equal(0, receiver.Available);
            LogQueryResult logs = await first.GetAsync<LogQueryResult>("logs.query", new LogQuery(WorkflowId: definition.Id, CausationId: request.OperationId), timeout.Token).ConfigureAwait(true);
            Assert.DoesNotContain(logs.Entries, entry => entry.Message is "workflow.unsuccessful");
        }
    }
}
