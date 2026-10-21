using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Core間で合流を越えた要求値と返信先が混線しないことを確認します。</summary>
public sealed class EventContextIntegrationTests
{
    /// <summary>先着Signalから開始した返信が、遅い別Peerの解析結果と元受付を待って使用します。</summary>
    /// <returns>並行HTTPの本文・ステータス・返信先をそれぞれ維持する確認です。</returns>
    [Fact]
    public async Task SignalChildRepliesWithItsOwnDelayedRemoteResultAsync()
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
            return status.GetProperty("peers").EnumerateArray().Count(item => item.GetProperty("status").GetString() is "ready") == 2;
        }).ConfigureAwait(true);
        int listenPort = CorePeer.AvailablePort(tcp: true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("causal-http", "Causal HTTP", [
            new("receive", "http.listen", JsonSerializer.SerializeToElement(new { interfaceId = second.InterfaceId, localAddress = "127.0.0.1", localPort = listenPort, path = "/echo", replyMode = "workflow" }),
                Target: new(EExecutionScope.One, ["b"]), StartOnLoad: true),
            new("accepted", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 0 }), Target: new(EExecutionScope.One, ["a"])),
            new("signal", "control.signal.each", empty, Target: new(EExecutionScope.One, ["a"])),
            new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 150 }), Target: new(EExecutionScope.One, ["b"])),
            new("parse", "data.json.parse", empty, Target: new(EExecutionScope.One, ["a"])),
            new("reply", "http.reply", empty, Target: new(EExecutionScope.One, SourcePeer: true)),
        ], [new("receive", "value", "accepted", "value"), new("accepted", CompletionPorts.Signal, "signal", "input1"),
            new("receive", "value", "delay", "value", Path: "/body"), new("delay", "value", "parse", "value"),
            new("signal", "start", "reply", "trigger"), new("receive", "request", "reply", "request"), new("parse", "value", "reply", "value")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved, ContractJson.Options));
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(item => item.GetProperty("nodeId").GetString() is "receive" && item.GetProperty("status").GetProperty("state").GetString() is "listening")).ConfigureAwait(true);
        using HttpClient external = new() { Timeout = TimeSpan.FromSeconds(20) };
        Uri address = new($"http://127.0.0.1:{listenPort}/echo");
        await Task.WhenAll(CheckReplyAsync(external, address, HttpStatusCode.Created, 7, 2), CheckReplyAsync(external, address, HttpStatusCode.Accepted, 9, 5)).ConfigureAwait(true);
    }

    /// <summary>独立したHTTP要求とその応答を一組として確認します。</summary>
    /// <returns>要求に対応する本文と応答コードの確認です。</returns>
    private static async Task CheckReplyAsync(HttpClient external, Uri address, HttpStatusCode status, int group, int zone, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await external.PostAsJsonAsync(address, new { statusCode = (int)status, body = new { group, zone } }, cancellationToken).ConfigureAwait(true);
        Assert.Equal(status, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(true);
        Assert.Equal((group, zone), (body.GetProperty("group").GetInt32(), body.GetProperty("zone").GetInt32()));
    }
}
