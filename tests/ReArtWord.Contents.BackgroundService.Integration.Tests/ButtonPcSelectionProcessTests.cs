using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>PC候補を実Core上のボタンへ渡し、押下ごとの宛先を実通信で検証します。</summary>
public sealed class ButtonPcSelectionProcessTests
{
    private static readonly string[] singleHost = ["host-b"];

    /// <summary>二口目と最終口の候補を合流させ、単一・複数PCへ対応する値だけを配送します。</summary>
    /// <returns>同じ保存版での繰り返し押下と、実際のPC別結果です。</returns>
    [Fact]
    public async Task PressedPcCandidatesRouteAcrossPeersWithoutMixingAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a", 1000), second = new(cluster, "b", port, false, "host-b", 900);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
        }, timeout.Token).ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("button-pcs", "Button execution PCs", [
            new("pcB", PcSelectionPorts.SelectNode, JsonSerializer.SerializeToElement(new { mode = "selected", hostIds = singleHost })),
            new("pcAll", PcSelectionPorts.SelectNode, JsonSerializer.SerializeToElement(new { mode = "all" })),
            new("single", ButtonValuePorts.NodeType, empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("single") }, Target: new(EExecutionScope.One, ["b"])),
            new("group", ButtonValuePorts.NodeType, empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("group") }, Target: new(EExecutionScope.One, ["a"])),
            new("pcs", ValueMergePorts.NodeType, empty), new("payload", ValueMergePorts.NodeType, empty),
            new("sink", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 25 }), PcExecution: EPcExecutionMode.All)],
            [new("pcB", "value", "single", "value2"), new("pcAll", "value", "group", "value64"),
                new("single", "value2", "pcs", "input1"), new("group", "value64", "pcs", "input2"),
                new("single", "value", "payload", "input1"), new("group", "value", "payload", "input2"),
                new("pcs", "value", "sink", PcSelectionPorts.Execution), new("pcs", "start", "sink", "trigger"), new("payload", "value", "sink", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        foreach (string button in new[] { "single", "group", "single" })
        {
            WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision, button);
            WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", request, timeout.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token =>
            {
                run = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
                return !run.Running;
            }, timeout.Token).ConfigureAwait(true);
            Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
            NodeExecutionReport sink = run.Report!.Nodes.Single(node => node.NodeId is "sink");
            string[] expected = button switch { "single" => ["b"], _ => ["a", "b"] };
            Assert.Equal(expected, sink.Targets.Select(target => target.PeerId).Order(StringComparer.Ordinal));
            Assert.Equal(expected.Length, sink.PcSelection!.CandidateHostIds.Count);
            Assert.All(sink.Targets, target => Assert.Equal(button, target.Result.Outputs["value"].GetString()));
            WorkflowRunStatus replay = await first.PostAsync<WorkflowRunStatus>("runs.start", request, timeout.Token).ConfigureAwait(true);
            Assert.Equal(run.Report.Id, replay.Report!.Id);
        }
    }
}
