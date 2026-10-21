using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>分岐後のPC候補が実Core間の配送先を変え、要求間では混ざらないことを確認します。</summary>
public sealed class DynamicPcProcessTests
{
    private static readonly string[] singleHost = ["host-a"];
    private static readonly string[] groupedHosts = ["host-a", "host-b"];

    /// <summary>単一PCと複数PCの候補を交互に採用し、同じWFの保存版で実行します。</summary>
    /// <returns>実プロセス、UDP管理、動的な宛先と選択根拠の確認です。</returns>
    [Fact]
    public async Task ConditionalCandidatesSelectSingleOrGroupedPcsPerRunAsync()
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
        WorkflowDefinition definition = new("dynamic-pc", "Conditional execution PCs", [
            new("source", "control.pass", empty), new("yes", "control.ifStart", empty), new("no", "control.ifNotStart", empty),
            new("pcA", PcSelectionPorts.SelectNode, JsonSerializer.SerializeToElement(new { mode = "selected", hostIds = singleHost })),
            new("pcB", PcSelectionPorts.SelectNode, JsonSerializer.SerializeToElement(new { mode = "selected", hostIds = groupedHosts })),
            new("merge", ValueMergePorts.NodeType, empty),
            new("sink", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 25 }), new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("payload") }, PcExecution: EPcExecutionMode.All)],
            [new("source", "value", "yes", "condition"), new("source", "value", "no", "condition"),
                new("yes", "start", "pcA", "trigger"), new("no", "start", "pcB", "trigger"),
                new("pcA", "value", "merge", "input1"), new("pcB", "value", "merge", "input2"),
                new("merge", "value", "sink", PcSelectionPorts.Execution), new("merge", "start", "sink", "trigger")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        foreach (bool choice in new[] { true, false, true, false })
        {
            Guid operation = Guid.NewGuid();
            WorkflowRunRequest request = new(operation, definition.Id, saved.Revision, "source", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(choice) });
            WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", request, timeout.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token =>
            {
                run = await first.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(true);
                return !run.Running;
            }, timeout.Token).ConfigureAwait(true);
            Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
            NodeExecutionReport sink = run.Report!.Nodes.Single(node => node.NodeId is "sink");
            string[] expected = ["a"];
            if (!choice)
            {
                expected = ["a", "b"];
            }
            Assert.Equal(expected, sink.Targets.Select(target => target.PeerId).Order(StringComparer.Ordinal));
            Assert.Equal(expected.Length, sink.PcSelection!.CandidateHostIds.Count);
            Assert.All(sink.Targets, target => Assert.Equal("payload", target.Result.Outputs["value"].GetString()));
        }
    }
}
