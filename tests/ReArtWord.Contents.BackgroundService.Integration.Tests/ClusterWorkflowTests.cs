using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>外部の実CoreService二つを通じて保存と実行の境界を確認します。</summary>
public sealed class ClusterWorkflowTests
{
    /// <summary>一時続行は自動停止を解除しますが、全Core停止後の再起動では失効します。</summary>
    /// <returns>明示復旧と全停止のいずれでも単体停止へ戻る確認です。</returns>
    [Fact]
    public async Task TemporaryContinuationExpiresAfterAllCoreInstancesRestart()
    {
        CorePeer core = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = ((IAsyncDisposable)core).ConfigureAwait(true);
        await core.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await core.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        WorkflowDefinition definition = new("temporary", "temporary continuation", [new NodeDefinition("pass", "control.pass", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(42) }, Safety: new NodeSafetyPolicy(StopWhenSingleton: true, Group: "show", Confirmation: ESafetyConfirmation.Continue))], []);
        WorkflowSaveResult saved = await core.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await RunAsync(core, new(Guid.NewGuid(), definition.Id, saved.Revision)).ConfigureAwait(true)).Outcome);
        ManualSafetyChange continuation = new(Guid.NewGuid(), definition.Id, "show", false, Continued: true);
        ManualSafetyPreview preview = await core.GetAsync<ManualSafetyPreview>("safety.preview", continuation).ConfigureAwait(true);
        Assert.True(preview.ConfirmationRequired);
        Assert.Equal("safety.singleton", Assert.Single(preview.Targets).StopReason);
        JsonElement refused = await core.PostAsync<JsonElement>("safety.change", continuation).ConfigureAwait(true);
        Assert.Equal("safety.confirmationRequired", refused.GetProperty("reason").GetString());
        continuation = continuation with { OperationId = Guid.NewGuid(), ImpactConfirmed = true, ExpectedWorkflowRevision = preview.WorkflowRevision };
        JsonElement changed = await core.PostAsync<JsonElement>("safety.change", continuation).ConfigureAwait(true);
        Assert.Equal("succeeded", changed.GetProperty("outcome").GetString());
        Assert.Equal(EOutcome.Succeeded, (await RunAsync(core, new(Guid.NewGuid(), definition.Id, saved.Revision)).ConfigureAwait(true)).Outcome);
        _ = await core.PostAsync<JsonElement>("safety.change", new ManualSafetyChange(Guid.NewGuid(), definition.Id, "show", false)).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, (await RunAsync(core, new(Guid.NewGuid(), definition.Id, saved.Revision)).ConfigureAwait(true)).Outcome);
        _ = await core.PostAsync<JsonElement>("safety.change", continuation with { OperationId = Guid.NewGuid() }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await RunAsync(core, new(Guid.NewGuid(), definition.Id, saved.Revision)).ConfigureAwait(true)).Outcome);
        await core.StopAsync().ConfigureAwait(true);
        await core.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await core.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        JsonElement state = await core.GetAsync<JsonElement>("safety.read").ConfigureAwait(true);
        JsonElement held = Assert.Single(state.GetProperty("states").EnumerateArray());
        Assert.False(held.GetProperty("continued").GetBoolean());
        Assert.Equal(EOutcome.NotExecuted, (await RunAsync(core, new(Guid.NewGuid(), definition.Id, saved.Revision)).ConfigureAwait(true)).Outcome);
    }

    /// <summary>別Peerで加工した応答を元受付から返し、別TCP経路で因果ログを集約します。</summary>
    /// <returns>実HTTP・UDP分散実行・TCPログの結合確認です。</returns>
    [Fact]
    public async Task RemoteProcessingRepliesFromSourcePeerAndForwardsCausationLogs()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        int listenPort = CorePeer.AvailablePort(tcp: true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("http-integration", "distributed reply",
        [
            new NodeDefinition("receive", "http.listen", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = listenPort, path = "/echo", replyMode = "workflow" }),
                Target: new TargetDefinition(EExecutionScope.One, ["b"]), StartOnLoad: true),
            new NodeDefinition("parse", "data.json.parse", empty, Target: new TargetDefinition(EExecutionScope.One, ["a"])),
            new NodeDefinition("reply", "http.reply", empty, Target: new TargetDefinition(EExecutionScope.One, SourcePeer: true)),
            new NodeDefinition("record", "control.log", JsonSerializer.SerializeToElement(new { level = "INFO", message = "external request completed" }), Target: new TargetDefinition(EExecutionScope.One, SourcePeer: true)),
        ],
        [
            new WireDefinition("receive", "value", "parse", "value", Path: "/body"),
            new WireDefinition("receive", "request", "reply", "request"),
            new WireDefinition("parse", "value", "reply", "value"),
            new WireDefinition("reply", "value", "record", "value"),
        ], Enabled: true);
        WorkflowSaveResult save = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, save.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(item => string.Equals(item.GetProperty("nodeId").GetString(), "receive", StringComparison.Ordinal)
                && string.Equals(item.GetProperty("status").GetProperty("state").GetString(), "listening", StringComparison.Ordinal))).ConfigureAwait(true);
        using HttpClient external = new() { Timeout = TimeSpan.FromSeconds(15) };
        using HttpResponseMessage response = await external.PostAsJsonAsync(new Uri($"http://127.0.0.1:{listenPort}/echo"), new { statusCode = 201, body = new { value = 42 } }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true);
        Assert.Equal(42, body.GetProperty("value").GetInt32());
        LogQueryResult? collected = null;
        await CorePeer.WaitAsync(async token =>
        {
            collected = await first.GetAsync<LogQueryResult>("logs.query", new LogQuery(WorkflowId: definition.Id, Peers: ["b"], NodeId: "record"), token).ConfigureAwait(true);
            return collected.Entries.Count == 1;
        }).ConfigureAwait(true);
        WorkflowLogEntry logged = Assert.Single(collected!.Entries);
        Assert.NotNull(logged.CausationId);
        Assert.Equal("b", logged.PeerId);
        LogQueryResult linked = await second.GetAsync<LogQueryResult>("logs.query", body: new LogQuery(CausationId: logged.CausationId)).ConfigureAwait(true);
        Assert.Equal(logged.Id, Assert.Single(linked.Entries).Id);
    }

    /// <summary>All配送、重複受付、手動停止の共有とPeer再起動後の維持を確認します。</summary>
    /// <returns>実UDP・HTTP・別プロセス永続化の結合確認です。</returns>
    [Fact]
    public async Task TwoProcessesShareWorkflowRunOnceAndKeepManualStopAfterRestart()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("integration", "two process", [new NodeDefinition("value", "control.pass", settings,
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(42) }, new TargetDefinition(EExecutionScope.All, ["a", "b"]))], []);
        WorkflowSaveResult save = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, save.Saved.Outcome);
        Assert.Equal(EOutcome.Succeeded, save.Shared.Outcome);
        Assert.Equal(EOutcome.Succeeded, save.Snapshot.Outcome);
        Assert.Equal(EOutcome.Succeeded, save.Activation.Outcome);
        JsonElement remote = await second.GetAsync<JsonElement>("workflows.read", body: "integration").ConfigureAwait(true);
        Assert.Equal(save.Revision, remote.GetProperty("saved").GetProperty("revision").GetString());
        WorkflowRunRequest run = new(Guid.NewGuid(), definition.Id, save.Revision);
        WorkflowRunStatus result = await RunAsync(first, run).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, Assert.Single(result.Report!.Nodes).Targets.Count);
        Assert.All(result.Report.Nodes[0].Targets, target => Assert.Equal(42, target.Result.Outputs["value"].Json.GetInt32()));
        WorkflowRunStatus duplicate = await first.PostAsync<WorkflowRunStatus>("runs.start", run).ConfigureAwait(true);
        Assert.Equal(ContractJson.Hash(result.Report), ContractJson.Hash(duplicate.Report));
        ManualSafetyChange stop = new(Guid.NewGuid(), definition.Id, "value", true);
        _ = await first.PostAsync<JsonElement>("safety.change", stop).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("safety.read", token).ConfigureAwait(true)).GetProperty("states").GetArrayLength() == 1).ConfigureAwait(true);
        Guid oldInstance = (await second.GetAsync<JsonElement>("health").ConfigureAwait(true)).GetProperty("instanceId").GetGuid();
        await second.StopAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first, second).ConfigureAwait(true);
        Assert.NotEqual(oldInstance, (await second.GetAsync<JsonElement>("health").ConfigureAwait(true)).GetProperty("instanceId").GetGuid());
        WorkflowRunStatus held = await RunAsync(first, run with { OperationId = Guid.NewGuid() }).ConfigureAwait(true);
        Assert.True(held.Outcome == EOutcome.NotExecuted, JsonSerializer.Serialize(held));
        Assert.All(Assert.Single(held.Report!.Nodes).Targets, target => Assert.Equal("safety.dropped", target.Result.ReasonCode));
    }

    /// <summary>受付と実終了を別々に確認します。</summary>
    /// <returns>元の操作IDの完了結果です。</returns>
    private static async Task<WorkflowRunStatus> RunAsync(CorePeer peer, WorkflowRunRequest request, CancellationToken cancellationToken = default)
    {
        WorkflowRunStatus current = await peer.PostAsync<WorkflowRunStatus>("runs.start", request, cancellationToken).ConfigureAwait(true);
        if (!current.Running)
        {
            return current;
        }
        await CorePeer.WaitAsync(async token =>
        {
            current = await peer.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !current.Running;
        }, cancellationToken).ConfigureAwait(true);
        return current;
    }

    /// <summary>両方の実Peerが互いの同期を確認するまで待ちます。</summary>
    /// <returns>二つの参加確認です。</returns>
    private static Task ReadyAsync(CorePeer first, CorePeer second, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
    {
        JsonElement a = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        JsonElement b = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        Guid firstInstance = a.GetProperty("instanceId").GetGuid();
        Guid secondInstance = b.GetProperty("instanceId").GetGuid();
        return a.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2
            && b.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2
            && a.GetProperty("peers").EnumerateArray().Any(item => item.GetProperty("instanceId").GetGuid() == secondInstance)
            && b.GetProperty("peers").EnumerateArray().Any(item => item.GetProperty("instanceId").GetGuid() == firstInstance);
    }, cancellationToken);
}
