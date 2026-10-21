using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>異なるSource所有Coreの同時入力も、予約した一回へ限定して記録します。</summary>
public sealed class WorkflowDebugIntegrationTests
{
    private static readonly string[] peers = ["a", "b"];

    /// <summary>実UDP入力と遠隔実行を二つのCoreで発火し、一方だけの診断を保持します。</summary>
    /// <returns>予約の自動消費、再送の冪等性、バイナリ非展開と実体別の記録です。</returns>
    [Fact]
    public async Task OnlyOneOfConcurrentPeerInputsClaimsTheCaptureAsync()
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
            JsonElement left = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            JsonElement right = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return left.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
                && right.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
        }).ConfigureAwait(true);
        int firstPort = CorePeer.AvailablePort(), secondPort = CorePeer.AvailablePort();
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("debug-inputs", "Debug inputs", [
            new("a", "udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = first.InterfaceId, localAddress = "127.0.0.1", localPort = firstPort }), Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true),
            new("b", "udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = secondPort }), Target: new(EExecutionScope.One, ["b"]), StartOnLoad: true),
            new("merge", "control.valueMerge", empty, Target: new(EExecutionScope.One, ["a"])),
            new("sink", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 25 }), Target: new(EExecutionScope.One, ["b"])),
        ], [new("a", "value", "merge", "input1"), new("b", "value", "merge", "input2"), new("merge", "value", "sink", "value")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement left = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            JsonElement right = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return left.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("nodeId").GetString() is "a" && source.GetProperty("status").GetProperty("state").GetString() is "listening")
                && right.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("nodeId").GetString() is "b" && source.GetProperty("status").GetProperty("state").GetString() is "listening");
        }).ConfigureAwait(true);
        WorkflowDebugRequest request = new(Guid.NewGuid(), definition.Id);
        WorkflowDebugView armed = await first.PostAsync<WorkflowDebugView>("workflow-debug.arm", request).ConfigureAwait(true);
        Assert.True(armed.State == EWorkflowDebugState.Armed, JsonSerializer.Serialize(armed));
        using UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] firstPayload = new byte[100], secondPayload = new byte[200];
        await Task.WhenAll(sender.SendAsync(firstPayload, new IPEndPoint(IPAddress.Loopback, firstPort)).AsTask(), sender.SendAsync(secondPayload, new IPEndPoint(IPAddress.Loopback, secondPort)).AsTask()).ConfigureAwait(true);
        WorkflowDebugView? captured = null;
        await CorePeer.WaitAsync(async token =>
        {
            captured = await first.GetAsync<WorkflowDebugView>("workflow-debug.read", new WorkflowDebugQuery(request.OperationId, Limit: 50), token).ConfigureAwait(true);
            return captured.State == EWorkflowDebugState.Completed;
        }).ConfigureAwait(true);
        Assert.NotNull(captured!.ExecutionId);
        Assert.Contains(captured.CapturePeerId, peers);
        Assert.Contains(captured.Nodes!, node => node.NodeId is "sink" && node.Outcome == EOutcome.Succeeded);
        string data = JsonSerializer.Serialize(captured);
        bool hundred = data.Contains("Binary (100 bytes)", StringComparison.Ordinal);
        bool twoHundred = data.Contains("Binary (200 bytes)", StringComparison.Ordinal);
        Assert.NotEqual(hundred, twoHundred);
        WorkflowDebugView same = await first.PostAsync<WorkflowDebugView>("workflow-debug.arm", request).ConfigureAwait(true);
        Assert.Equal(captured.ExecutionId, same.ExecutionId);
        WorkflowDebugRequest next = new(Guid.NewGuid(), definition.Id);
        Assert.Equal(EWorkflowDebugState.Armed, (await first.PostAsync<WorkflowDebugView>("workflow-debug.arm", next).ConfigureAwait(true)).State);
        Assert.Equal(EWorkflowDebugState.Cancelled, (await first.PostAsync<WorkflowDebugView>("workflow-debug.cancel", next.OperationId).ConfigureAwait(true)).State);
    }
}
