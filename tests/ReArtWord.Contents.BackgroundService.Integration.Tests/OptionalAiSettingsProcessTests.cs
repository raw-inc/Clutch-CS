using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>任意設定の省略と動的PC選択を、別Coreへの実配送とHTTP要求で確認します。</summary>
public sealed class OptionalAiSettingsProcessTests
{
    /// <summary>ForEachの各要素を別PCのAIノードへ渡し、受信した実HTTP本文を検証します。</summary>
    /// <returns>外部サービスへ接続しない二つのCoreの結合試験です。</returns>
    [Fact]
    public async Task OptionalParametersReachAnotherPeerWithoutNullOrPcErrorsAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a"), second = new(cluster, "b", port, false, "host-b");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        int httpPort = CorePeer.AvailablePort(tcp: true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition endpoint = new("local-ai", "Local AI fixture", [
            new("receive", "http.listen", JsonSerializer.SerializeToElement(new { interfaceId = first.InterfaceId, localAddress = "127.0.0.1", localPort = httpPort, path = "/generate", replyMode = "workflow" }),
                Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true),
            new("reply", "http.reply", empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(new
            {
                statusCode = 200,
                body = new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "local result" } } } } },
            }) }, Target: new(EExecutionScope.One, SourcePeer: true))], [new("receive", "request", "reply", "request")], Enabled: true);
        WorkflowSaveResult endpointSaved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), endpoint, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, endpointSaved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => source.GetProperty("workflowId").GetString() is "local-ai" && source.GetProperty("status").GetProperty("state").GetString() is "listening"), timeout.Token).ConfigureAwait(true);
        WorkflowDefinition caller = new("optional-ai", "Optional AI", [new("button", "control.button", empty, Target: new(EExecutionScope.One, ["a"])),
            new("pc", "execution.pc.select", JsonSerializer.Deserialize<JsonElement>("""{"mode":"selected","hostIds":["host-b"]}""")),
            new("merge", "control.valueMerge", empty), new("loop", "control.forEach", empty, Target: new(EExecutionScope.One, ["a"])),
            new("ai", "ai.openai.responses", JsonSerializer.SerializeToElement(new
            {
                model = "local-fixture", endpoint = $"http://127.0.0.1:{httpPort}/generate", interfaceId = second.InterfaceId, localAddress = "127.0.0.1", timeoutMs = 10000,
                instructions = "old", topP = 0.8, store = true,
            })), new("end", "control.forEach.end", empty, Target: new(EExecutionScope.One, ["a"]))],
            [new("button", "start", "pc", "trigger"), new("pc", "value", "merge", "input1"), new("merge", "value", "ai", "$execution.pc"),
                new("button", "value", "loop", "value"), new("button", "start", "loop", "trigger"), new("loop", "item/text", "ai", "value"),
                new("loop", "item/instructions", "ai", "$settings.instructions"), new("loop", "item/topP", "ai", "$settings.topP"), new("loop", "item/store", "ai", "$settings.store"),
                new("loop", "loop", "end", "loop"), new("ai", "value", "end", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), caller, null), timeout.Token).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        NodeValue items = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("""
            [{"text":"missing"},{"text":"null","instructions":null,"topP":null,"store":null},{"text":"explicit","instructions":"calm","topP":0,"store":false}]
            """));
        WorkflowRunRequest request = new(Guid.NewGuid(), caller.Id, saved.Revision, "button", new Dictionary<string, NodeValue> { ["value"] = items });
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", request, timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }, timeout.Token).ConfigureAwait(true);
        Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
        ExecutionHistoryPage history = await first.GetAsync<ExecutionHistoryPage>("executions.list", new ExecutionHistoryQuery(), timeout.Token).ConfigureAwait(true);
        JsonElement[] received = history.Entries.Where(entry => entry.Report?.WorkflowId is "local-ai").SelectMany(entry => entry.Report!.Nodes)
            .Where(node => node.NodeId is "receive").SelectMany(node => node.Targets).Where(target => target.Result.Outputs.ContainsKey("value"))
            .Select(target => JsonSerializer.Deserialize<JsonElement>(target.Result.Outputs["value"].Json.GetProperty("body").GetString()!)).ToArray();
        Assert.Equal(3, received.Length);
        foreach (JsonElement body in received)
        {
            bool specified = body.GetProperty("input").GetString() is "explicit";
            foreach (string name in new[] { "instructions", "top_p", "store" })
            {
                Assert.Equal(specified, body.TryGetProperty(name, out _));
            }
            if (specified)
            {
                Assert.Equal(0, body.GetProperty("top_p").GetInt32());
                Assert.False(body.GetProperty("store").GetBoolean());
            }
        }
        NodeExecutionReport executedAi = Assert.Single(run.Report!.Nodes, node => node.NodeId is "ai");
        Assert.NotEmpty(executedAi.Targets);
        Assert.All(executedAi.Targets, target => Assert.Equal("b", target.PeerId));
    }
}
