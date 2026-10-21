using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>Source取消が別Coreで受付済みのノードへ届くことを実通信で確認します。</summary>
public sealed class SourceCancellationTests
{
    /// <summary>受信元を止めた後も遠隔の長時間ノードだけが残る状態を防ぎます。</summary>
    /// <returns>UDP入力・別Core実行・明示取消・実停止の確認です。</returns>
    [Fact]
    public async Task ExplicitSourceCancellationReachesAdmittedNodeOnOtherCore()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2).ConfigureAwait(true);
        int inputPort = CorePeer.AvailablePort();
        WorkflowDefinition definition = new("source-cancel", "remote source cancellation",
        [
            new NodeDefinition("receive", "udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = inputPort }),
                Target: new TargetDefinition(EExecutionScope.One, ["b"]), StartOnLoad: true),
            new NodeDefinition("waiting", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 60000 }), Target: new TargetDefinition(EExecutionScope.One, ["a"])),
        ], [new WireDefinition("receive", "value", "waiting", "value")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => string.Equals(source.GetProperty("nodeId").GetString(), "receive", StringComparison.Ordinal)
                && string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "listening", StringComparison.Ordinal))).ConfigureAwait(true);
        using UdpClient input = new(new IPEndPoint(IPAddress.Loopback, 0));
        await input.SendAsync(new byte[] { 1, 2 }, new IPEndPoint(IPAddress.Loopback, inputPort)).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("runningNodes").GetArrayLength() == 1).ConfigureAwait(true);
        JsonElement beforeStop = await second.GetAsync<JsonElement>("status").ConfigureAwait(true);
        Guid sourceId = beforeStop.GetProperty("sources").EnumerateArray().Single(source => string.Equals(source.GetProperty("nodeId").GetString(), "receive", StringComparison.Ordinal)).GetProperty("id").GetGuid();
        _ = await second.PostAsync<JsonElement>("sources.stop", new SourceStopRequest(definition.Id, "receive", true, sourceId, beforeStop.GetProperty("instanceId").GetGuid())).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("runningNodes").GetArrayLength() == 0).ConfigureAwait(true);
        JsonElement stopped = await second.GetAsync<JsonElement>("status").ConfigureAwait(true);
        Assert.DoesNotContain(stopped.GetProperty("sources").EnumerateArray(), source => string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "listening", StringComparison.Ordinal));
    }
}
