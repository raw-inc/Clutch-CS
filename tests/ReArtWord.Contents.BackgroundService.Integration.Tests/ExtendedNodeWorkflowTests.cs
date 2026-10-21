using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>新ノードを通常の分散グラフへ配線し、実通信と実実行先を確認します。</summary>
public sealed class ExtendedNodeWorkflowTests
{
    /// <summary>WebSocket受信を平坦化・C#処理し、元Peerから返信します。業務SQLiteも別Peerで読みます。</summary>
    /// <returns>カタログ・型・保存・実行・返信の利用経路です。</returns>
    [Fact]
    public async Task WebSocketCollectionsScriptsAndSqliteRunAcrossPeersAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        string? published = Environment.GetEnvironmentVariable("CONTENTS_TEST_CORE_EXECUTABLE");
        CorePeer first = new(cluster, "a", port, true, executablePath: published);
        CorePeer second = new(cluster, "b", port, false, executablePath: published);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
        }).ConfigureAwait(true);
        string code = "public static long Run(long[] values) => values.Sum();";
        ScriptAnalysis analysis = await first.GetAsync<ScriptAnalysis>("scripts.analyze", new ScriptAnalysisRequest("csharp", code)).ConfigureAwait(true);
        Assert.NotNull(analysis.Contract);
        JsonElement script = JsonSerializer.SerializeToElement(new { code, analysis }, ContractJson.Options);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        int socketPort = CorePeer.AvailablePort(tcp: true);
        WorkflowDefinition socket = new("extended-socket", "Socket and script", [
            new("receive", "websocket.listen", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = socketPort, path = "/sum", replyMode = "workflow", count = 1 }), Target: new(EExecutionScope.One, ["b"]), StartOnLoad: true),
            new("parse", "data.json.parse", empty, Target: new(EExecutionScope.One, ["a"])),
            new("flatten", "data.flatten", empty),
            new("arguments", "value.keyValue", JsonSerializer.SerializeToElement(new { key = "values" })),
            new("script", "script.csharp", script, Target: new(EExecutionScope.One, ["a"])),
            new("reply", "websocket.reply", empty, Target: new(EExecutionScope.One, SourcePeer: true)),
        ], [new("receive", "value", "parse", "value"), new("parse", "value", "flatten", "value"), new("flatten", "value", "arguments", "value"),
            new("arguments", "value", "script", "arguments"), new("receive", "request", "reply", "request"), new("script", "value", "reply", "value")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), socket, null)).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved, ContractJson.Options));
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => string.Equals(source.GetProperty("nodeId").GetString(), "receive", StringComparison.Ordinal) && string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "listening", StringComparison.Ordinal))).ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        using ClientWebSocket external = new();
        await external.ConnectAsync(new Uri($"ws://127.0.0.1:{socketPort}/sum"), deadline.Token).ConfigureAwait(true);
        await external.SendAsync("[[1,2],[3]]"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(true);
        byte[] buffer = new byte[64];
        ValueWebSocketReceiveResult reply = await external.ReceiveAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(true);
        Assert.True(reply.EndOfMessage);
        Assert.Equal("6", Encoding.UTF8.GetString(buffer, 0, reply.Count));

        string database = Path.Combine(second.DirectoryPath, "content", "business.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        WorkflowDefinition sql = new("extended-sql", "Remote SQLite", [new("query", "database.sqlite",
            JsonSerializer.SerializeToElement(new { path = database, createIfMissing = true, sql = "SELECT @value AS value", mode = "query" }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["parameters"] = NodeValue.FromObject(new { value = 42 }) }, Target: new(EExecutionScope.One, ["b"]))], []);
        saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), sql, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunRequest run = new(Guid.NewGuid(), sql.Id, saved.Revision);
        await first.PostAsync<WorkflowRunStatus>("runs.start", run).ConfigureAwait(true);
        WorkflowRunStatus? completed = null;
        await CorePeer.WaitAsync(async token =>
        {
            completed = await first.GetAsync<WorkflowRunStatus>("runs.read", run.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !completed.Running;
        }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, completed!.Outcome);
        TargetNodeResult result = Assert.Single(Assert.Single(completed.Report!.Nodes).Targets);
        Assert.Equal("b", result.PeerId);
        Assert.Equal(42, result.Result.Outputs["value"].Json[0].GetProperty("value").GetInt32());
        Assert.True(File.Exists(database));
    }
}
