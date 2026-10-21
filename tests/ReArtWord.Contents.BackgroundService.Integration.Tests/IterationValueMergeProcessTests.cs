using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>同じ要素の分岐を別Coreで処理し、値合流とEnd収集まで実通信で確認します。</summary>
public sealed class IterationValueMergeProcessTests
{
    /// <summary>分岐の実行PCが異なっても、元の要素のキーと値を混ぜません。</summary>
    /// <returns>二つの実Coreによる保存・実行・反復結果の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ValuesReturnToTheirElementAcrossPeersAsync(bool parallel, bool pendingUnused)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
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
        string loop = IterationNodeTypes.ForEach;
        if (parallel)
        {
            loop = IterationNodeTypes.ParallelForEach;
        }
        WorkflowDefinition definition = new("element-merge", "Element merge", [
            new("button", "control.button", empty, Target: new(EExecutionScope.One, ["a"])), new("loop", loop, empty),
            new("yes", "control.ifStart", empty), new("no", "control.ifNotStart", empty),
            new("left", "control.pass", empty, Target: new(EExecutionScope.One, ["a"])), new("right", "control.pass", empty, Target: new(EExecutionScope.One, ["b"])),
            new("merge", "control.valueMerge", empty, Target: new(EExecutionScope.One, ["b"])),
            new("pair", "value.keyValue", empty), new("end", IterationNodeTypes.End, empty)],
            [new("button", "value", "loop", "value"), new("button", "start", "loop", "trigger"),
                new("loop", "item/selected", "yes", "condition"), new("loop", "item/selected", "no", "condition"),
                new("yes", "start", "left", "trigger"), new("no", "start", "right", "trigger"),
                new("loop", "item/voice", "left", "value"), new("loop", "item/voice", "right", "value"),
                new("left", "value", "merge", "input1"), new("right", "value", "merge", "input2"),
                new("loop", "item/key", "pair", "key"), new("merge", "value", "pair", "value"), new("merge", "start", "pair", "trigger"),
                new("loop", "loop", "end", "loop"), new("pair", "value", "end", "value")]);
        if (pendingUnused)
        {
            string left = "left", value = "value";
            definition = definition with
            {
                Nodes = [.. definition.Nodes.Select(node => node.Id switch { "left" => node with { Type = "data.concat" }, _ => node }),
                    new("otherButton", "control.button", empty), new("otherEvent", "control.valueMerge", empty)],
                Wires = [.. definition.Wires.Select(wire =>
                {
                    if (string.Equals(wire.ToNode, left, StringComparison.Ordinal) && string.Equals(wire.ToPort, value, StringComparison.Ordinal))
                    {
                        return wire with { ToPort = "input1" };
                    }
                    return wire;
                }), new("otherButton", "value", "otherEvent", "input1"), new("otherEvent", "value", "left", "input2")],
            };
        }
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        NodeValue items = NodeValue.FromObject(new[] { new { key = "one", voice = "voice-one", selected = !pendingUnused }, new { key = "two", voice = "voice-two", selected = false },
            new { key = "three", voice = "voice-three", selected = !pendingUnused }, new { key = "four", voice = "voice-four", selected = false } });
        WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision, "button", new Dictionary<string, NodeValue> { ["value"] = items });
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", request, timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }, timeout.Token).ConfigureAwait(true);
        Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
        JsonElement values = Assert.Single(run.Report!.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs["value"].Json;
        Assert.Equal(4, values.EnumerateObject().Count());
        foreach (JsonElement item in items.Json.EnumerateArray())
        {
            Assert.Equal(item.GetProperty("voice").GetString(), values.GetProperty(item.GetProperty("key").GetString()!).GetString());
        }
        Assert.Equal("a", Assert.Single(run.Report.Nodes.Single(node => node.NodeId is "left").Targets).PeerId);
        Assert.Equal("b", Assert.Single(run.Report.Nodes.Single(node => node.NodeId is "right").Targets).PeerId);
        Assert.Equal("b", Assert.Single(run.Report.Nodes.Single(node => node.NodeId is "merge").Targets).PeerId);
        Assert.Equal(4, run.Report.Nodes.Single(node => node.NodeId is "loop").Iteration!.Started);
        if (pendingUnused)
        {
            Assert.Equal(EOutcome.NotExecuted, run.Report.Nodes.Single(node => node.NodeId is "left").Outcome);
            Assert.All(run.Report.Nodes.Single(node => node.NodeId is "left").Targets, target => Assert.Equal(ExecutionResultReasons.BranchNotSelected, target.Result.ReasonCode));
        }
    }
}
