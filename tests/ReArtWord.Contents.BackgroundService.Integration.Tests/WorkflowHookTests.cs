using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別Coreの共通Hookを購読し、購読側の処理が作る通知の循環を防ぐことを確認します。</summary>
public sealed class WorkflowHookTests
{
    /// <summary>別Peerの二件のログを受け取り、Hook自身のログで購読回数を消費しません。</summary>
    /// <returns>実ログ出力、Peer間転送、再入防止、正常終了の確認。</returns>
    [Theory]
    [InlineData("log.written")]
    [InlineData("node.completed")]
    [InlineData("workflow.completed")]
    public async Task RemoteHooksPreserveOriginAndDoNotConsumeTheirOwnEventsAsync(string eventName)
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
        TargetDefinition remote = new(EExecutionScope.One, ["b"]);
        WorkflowDefinition producer = new("producer", "Original log", [new("log", "control.log", JsonSerializer.SerializeToElement(new { message = "original", level = "WARN" }), Target: remote)], []);
        WorkflowDefinition consumer = new("consumer", "Observe remote logs", [
            new("hook", "control.hook", JsonSerializer.SerializeToElement(new { name = eventName, peer = "b", count = 2 }), Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true),
            new("echo", "control.log", JsonSerializer.SerializeToElement(new { message = "echo", level = "WARN" }), Target: remote),
        ], [new("hook", "value", "echo", "value")], Enabled: true);
        foreach (WorkflowDefinition definition in new[] { producer, consumer })
        {
            WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
            await CorePeer.WaitAsync(async token => string.Equals((await second.GetAsync<JsonElement>("workflows.read", definition.Id, token).ConfigureAwait(true))
                .GetProperty("effectiveRevision").GetString(), saved.Revision, StringComparison.Ordinal)).ConfigureAwait(true);
        }
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => string.Equals(source.GetProperty("nodeId").GetString(), "hook", StringComparison.Ordinal) && string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "listening", StringComparison.Ordinal))).ConfigureAwait(true);
        for (int index = 1; index <= 2; index++)
        {
            WorkflowRunStatus started = await second.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.NewGuid(), producer.Id)).ConfigureAwait(true);
            Assert.True(started.Running || started.Outcome == EOutcome.Succeeded, started.Reason);
            await CorePeer.WaitAsync(async token => index <= (await ReceivedAsync(first, token).ConfigureAwait(true)).Count).ConfigureAwait(true);
        }
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => string.Equals(source.GetProperty("nodeId").GetString(), "hook", StringComparison.Ordinal) && string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "completed", StringComparison.Ordinal))).ConfigureAwait(true);
        IReadOnlyList<HookDelivery> received = await ReceivedAsync(first).ConfigureAwait(true);
        Assert.Equal(2, received.Count);
        string logEvent = "log.written";
        string nodeEvent = "node.completed";
        string workflowEvent = "workflow.completed";
        Assert.All(received, entry =>
        {
            Assert.Equal("producer", entry.Event.WorkflowId);
            Assert.Equal("b", entry.Event.PeerId);
            Assert.NotNull(entry.Event.PeerInstanceId);
            Assert.NotNull(entry.Event.CausationId);
            Assert.Equal(eventName, entry.Event.Name);
            Assert.Equal(JsonValueKind.Object, entry.Data.ValueKind);
            if (string.Equals(eventName, logEvent, StringComparison.Ordinal))
            {
                Assert.Equal("original", entry.Data.GetProperty("message").GetString());
            }
            if (string.Equals(eventName, nodeEvent, StringComparison.Ordinal))
            {
                Assert.Equal("succeeded", entry.Data.GetProperty("result").GetProperty("outcome").GetString());
            }
            if (string.Equals(eventName, workflowEvent, StringComparison.Ordinal))
            {
                Assert.Equal("producer", entry.Data.GetProperty("report").GetProperty("workflowId").GetString());
            }
        });
    }

    /// <summary>Sourceから下流へ実際に流れたログだけを、元の実行結果から取得します。</summary>
    /// <returns>購読した元ログ。</returns>
    private static async Task<IReadOnlyList<HookDelivery>> ReceivedAsync(CorePeer peer, CancellationToken cancellationToken = default)
    {
        ExecutionHistoryPage history = await peer.GetAsync<ExecutionHistoryPage>("executions.list", cancellationToken).ConfigureAwait(true);
        return history.Entries.Where(item => string.Equals(item.Report?.WorkflowId, "consumer", StringComparison.Ordinal)).SelectMany(item => item.Report!.Nodes)
            .Where(node => string.Equals(node.NodeId, "hook", StringComparison.Ordinal)).SelectMany(node => node.Targets)
            .Where(target => target.Result.Outcome == EOutcome.Succeeded && target.Result.Outputs.ContainsKey("value") && target.Result.Outputs.ContainsKey("event"))
            .Select(target => new HookDelivery(target.Result.Outputs["event"].Json.Deserialize<WorkflowEvent>(ContractJson.Options)!, target.Result.Outputs["value"].Json)).ToArray();
    }

    /// <summary>購読結果の発生元と値を別々に確認します。</summary>
    /// <param name="Event">発生元と因果情報。</param>
    /// <param name="Data">購読により得た値。</param>
    private sealed record HookDelivery(WorkflowEvent Event, JsonElement Data);
}
