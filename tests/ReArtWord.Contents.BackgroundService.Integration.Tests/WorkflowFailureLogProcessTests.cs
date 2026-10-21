using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>別Coreでの通信失敗と所有CoreでのWF終了が同じ因果IDのログへ残ることを確認します。</summary>
public sealed class WorkflowFailureLogProcessTests
{
    /// <summary>外部APIを使わず、二つの実プロセスで失敗理由の転送と永続ログを検証します。</summary>
    /// <returns>送信元と実行先の診断確認です。</returns>
    [Fact]
    public async Task RemoteAiFailureAndWorkflowTerminationAreLoggedWithoutTraceAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        string? executable = Environment.GetEnvironmentVariable("CONTENTS_TEST_WORKFLOW_CORE");
        CorePeer first = new(cluster, "a", port, true, executablePath: executable), second = new(cluster, "b", port, false, executablePath: executable);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        using Socket reservation = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int httpPort = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        WorkflowDefinition definition = new("failure-log", "Communication failure", [new("ai", "ai.openai.responses", JsonSerializer.SerializeToElement(new
        {
            model = "local-fixture", endpoint = $"http://127.0.0.1:{httpPort}/private-path?secret=private-query",
            interfaceId = second.InterfaceId, localAddress = "127.0.0.1", timeoutMs = 5000,
        }), new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("private-prompt") }, Target: new(EExecutionScope.One, ["b"]))], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => string.Equals((await second.GetAsync<JsonElement>("workflows.read", definition.Id, token).ConfigureAwait(true))
            .GetProperty("effectiveRevision").GetString(), saved.Revision, StringComparison.Ordinal), timeout.Token).ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        _ = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(id, definition.Id, saved.Revision), timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<WorkflowRunStatus>("runs.read", id, token).ConfigureAwait(true)).Running == false, timeout.Token).ConfigureAwait(true);
        LogQuery query = new(WorkflowId: definition.Id, CausationId: id);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<LogQueryResult>("logs.query", query, token).ConfigureAwait(true)).Entries.Any(entry => entry.Message is "workflow.unsuccessful")
            && (await second.GetAsync<LogQueryResult>("logs.query", query, token).ConfigureAwait(true)).Entries.Any(entry => entry.Message is "ai.incomplete"), timeout.Token).ConfigureAwait(true);
        WorkflowLogEntry summary = Assert.Single((await first.GetAsync<LogQueryResult>("logs.query", query, timeout.Token).ConfigureAwait(true)).Entries, entry => entry.Message is "workflow.unsuccessful");
        WorkflowLogEntry node = Assert.Single((await second.GetAsync<LogQueryResult>("logs.query", query, timeout.Token).ConfigureAwait(true)).Entries, entry => entry.Message is "ai.incomplete");
        WorkflowLogEntry exception = Assert.Single((await second.GetAsync<LogQueryResult>("logs.query", query, timeout.Token).ConfigureAwait(true)).Entries, entry => entry.Message is "node.exception");
        Assert.Equal("ai", exception.NodeId);
        Assert.Contains("StackTrace", exception.Data!.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("SocketException", exception.Data.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("a", summary.PeerId);
        Assert.Equal("b", node.PeerId);
        Assert.Equal("ai", node.NodeId);
        Assert.Contains("ConnectionRefused", node.Data!.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("ai.incomplete", summary.Data!.Json.GetRawText(), StringComparison.Ordinal);
        string recorded = JsonSerializer.Serialize(new[] { summary, node, exception });
        Assert.DoesNotContain("private-query", recorded, StringComparison.Ordinal);
        Assert.DoesNotContain("private-prompt", recorded, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", recorded, StringComparison.Ordinal);
    }

    /// <summary>別PCのスクリプト例外と終了ログを同じ実行IDで検索できます。</summary>
    /// <returns>実通信と永続ファイルを経た例外診断です。</returns>
    [Fact]
    public async Task RemoteScriptExceptionIsPersistedWithItsOriginalNodeAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        string? executable = Environment.GetEnvironmentVariable("CONTENTS_TEST_WORKFLOW_CORE");
        CorePeer first = new(cluster, "a", port, true, executablePath: executable), second = new(cluster, "b", port, false, executablePath: executable);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        string code = "public static int Run(int value) => 42 / value;";
        Contracts.Scripting.ScriptAnalysis analysis = await second.GetAsync<Contracts.Scripting.ScriptAnalysis>("scripts.analyze", new Contracts.Scripting.ScriptAnalysisRequest("csharp", code), timeout.Token).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        WorkflowDefinition definition = new("script-failure-log", "Script failure", [new("script", "script.csharp", JsonSerializer.SerializeToElement(new { code, analysis }, Contracts.Serialization.ContractJson.Options),
            new Dictionary<string, NodeValue> { ["arguments"] = NodeValue.FromObject(new { value = 0 }) }, Target: new(EExecutionScope.One, ["b"]))], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => string.Equals((await second.GetAsync<JsonElement>("workflows.read", definition.Id, token).ConfigureAwait(true))
            .GetProperty("effectiveRevision").GetString(), saved.Revision, StringComparison.Ordinal), timeout.Token).ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        _ = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(id, definition.Id, saved.Revision), timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<WorkflowRunStatus>("runs.read", id, token).ConfigureAwait(true)).Running == false, timeout.Token).ConfigureAwait(true);
        LogQuery query = new(WorkflowId: definition.Id, CausationId: id);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<LogQueryResult>("logs.query", query, token).ConfigureAwait(true)).Entries.Any(entry => entry.Message is "node.exception")
            && (await first.GetAsync<LogQueryResult>("logs.query", query, token).ConfigureAwait(true)).Entries.Any(entry => entry.Message is "workflow.unsuccessful"), timeout.Token).ConfigureAwait(true);
        WorkflowLogEntry exception = Assert.Single((await second.GetAsync<LogQueryResult>("logs.query", query, timeout.Token).ConfigureAwait(true)).Entries, entry => entry.Message is "node.exception");
        Assert.Equal("b", exception.PeerId);
        Assert.Equal("script", exception.NodeId);
        Assert.Contains("DivideByZeroException", exception.Data!.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("WorkflowScript.Run", exception.Data.Json.GetRawText(), StringComparison.Ordinal);
        WorkflowLogEntry summary = Assert.Single((await first.GetAsync<LogQueryResult>("logs.query", query, timeout.Token).ConfigureAwait(true)).Entries, entry => entry.Message is "workflow.unsuccessful");
        Assert.Contains("script.executionFailed", summary.Data!.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("\"peerId\":\"b\"", summary.Data.Json.GetRawText(), StringComparison.Ordinal);
        Assert.True((await first.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true)).GetProperty("ready").GetBoolean());
        Assert.True((await second.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true)).GetProperty("ready").GetBoolean());
    }
}
