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

/// <summary>条件成立時の正常終了と、受付済み入力の継続を別Core間で確認します。</summary>
public sealed class SourceEndTests
{
    /// <summary>条件が成立したときだけ元Sourceを止め、同じ入力の別経路を完了させます。</summary>
    /// <returns>入力・分岐・正常終了・受付済み処理の完了確認。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConditionEndsOnlyTheOriginalSourceWithoutCancellingAcceptedWorkAsync(bool end)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2
                && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null;
        }).ConfigureAwait(true);
        int inputPort = CorePeer.AvailablePort();
        TargetDefinition remote = new(EExecutionScope.One, ["a"]);
        WorkflowDefinition definition = new("conditional-end", "Conditional source end", [
            new("receive", "udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = inputPort }), Target: new(EExecutionScope.One, ["b"]), StartOnLoad: true),
            new("condition", "control.condition", JsonSerializer.SerializeToElement(new { operation = "boolean" }), new Dictionary<string, NodeValue> { ["comparison"] = NodeValue.FromObject(end) }, Target: remote),
            new("end", "control.source.end", JsonSerializer.SerializeToElement(new { }), Target: remote),
            new("drain", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 600 }), Target: remote),
            new("completed", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: remote),
        ], [new("receive", "source", "condition", "value"), new("condition", "value", "end", "source", When: "true"),
            new("receive", "value", "drain", "value"), new("receive", "source", "completed", "value", When: "completed")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "listening", StringComparison.Ordinal))).ConfigureAwait(true);
        using UdpClient input = new(new IPEndPoint(IPAddress.Loopback, 0));
        await input.SendAsync(new byte[] { 1, 2 }, new IPEndPoint(IPAddress.Loopback, inputPort)).ConfigureAwait(true);
        ExecutionReport? report = null;
        await CorePeer.WaitAsync(async token =>
        {
            ExecutionHistoryPage history = await second.GetAsync<ExecutionHistoryPage>("executions.list", token).ConfigureAwait(true);
            report = history.Entries.Select(item => item.Report).FirstOrDefault(item => item?.Nodes.Any(node => string.Equals(node.NodeId, "drain", StringComparison.Ordinal) && node.Outcome == EOutcome.Succeeded) == true);
            return report is not null;
        }).ConfigureAwait(true);
        NodeExecutionReport ended = report!.Nodes.Single(node => string.Equals(node.NodeId, "end", StringComparison.Ordinal));
        if (end)
        {
            Assert.Equal(EOutcome.Succeeded, ended.Outcome);
            await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
                .Any(source => string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "completed", StringComparison.Ordinal))).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token => (await second.GetAsync<ExecutionHistoryPage>("executions.list", token).ConfigureAwait(true)).Entries
                .Any(item => item.Report?.Nodes.Any(node => string.Equals(node.NodeId, "completed", StringComparison.Ordinal) && node.Outcome == EOutcome.Succeeded) == true)).ConfigureAwait(true);
        }
        else
        {
            Assert.Equal(EOutcome.NotExecuted, ended.Outcome);
            JsonElement source = Assert.Single((await second.GetAsync<JsonElement>("status").ConfigureAwait(true)).GetProperty("sources").EnumerateArray());
            Assert.Equal("listening", source.GetProperty("status").GetProperty("state").GetString());
        }
    }
}
