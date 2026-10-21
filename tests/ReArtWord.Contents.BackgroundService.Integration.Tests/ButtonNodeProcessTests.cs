using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>押下要求を実Coreへ送り、別Peerへ値を渡して同一操作の再送を抑止します。</summary>
public sealed class ButtonNodeProcessTests
{
    /// <summary>有効化では押さず、押下操作IDごとに一件だけ配送します。</summary>
    /// <returns>保存・実通信・実行結果・再照会の確認です。</returns>
    [Fact]
    public async Task PressRunsAcrossPeersAndReplayedRequestKeepsOriginalExecution()
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
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
                && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null;
        }).ConfigureAwait(true);
        WorkflowDefinition definition = new("button-process", "Button process", [
            new("button", "control.button", JsonSerializer.SerializeToElement(new { }),
                new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("payload"), ["value2"] = NodeValue.FromObject(42), ["value3"] = NodeValue.FromObject(new { enabled = false }), ["label"] = NodeValue.FromObject("Go") }, Target: new(EExecutionScope.One, ["b"])),
            new("target", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["a"])),
            new("number", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["a"])),
            new("property", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["a"])),
            new("empty", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["a"])),
        ], [new("button", "start", "target", "trigger"), new("button", "value", "target", "value"),
            new("button", "start", "number", "trigger"), new("button", "value2", "number", "value"),
            new("button", "start", "property", "trigger"), new("button", "value3/enabled", "property", "value"),
            new("button", "start", "empty", "trigger"), new("button", "value64", "empty", "value")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision, "button");
        _ = await first.PostAsync<WorkflowRunStatus>("runs.start", request).ConfigureAwait(true);
        WorkflowRunStatus? completed = null;
        await CorePeer.WaitAsync(async token =>
        {
            completed = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !completed.Running && completed.Report is not null;
        }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, completed!.Outcome);
        TargetNodeResult button = Assert.Single(completed.Report!.Nodes.Single(node => node.NodeId is "button").Targets);
        Assert.Equal("b", button.PeerId);
        Assert.True(button.Result.Outputs["start"].Json.GetBoolean());
        TargetNodeResult target = Assert.Single(completed.Report.Nodes.Single(node => node.NodeId is "target").Targets);
        Assert.Equal("a", target.PeerId);
        Assert.Equal("payload", target.Result.Outputs["value"].GetString());
        Assert.Equal(42, Assert.Single(completed.Report.Nodes.Single(node => node.NodeId is "number").Targets).Result.Outputs["value"].Json.GetInt32());
        Assert.False(Assert.Single(completed.Report.Nodes.Single(node => node.NodeId is "property").Targets).Result.Outputs["value"].Json.GetBoolean());
        Assert.Equal(JsonValueKind.Null, Assert.Single(completed.Report.Nodes.Single(node => node.NodeId is "empty").Targets).Result.Outputs["value"].Json.ValueKind);
        Assert.Equal(5, button.Result.Outputs.Count);
        WorkflowRunStatus replay = await first.PostAsync<WorkflowRunStatus>("runs.start", request).ConfigureAwait(true);
        Assert.False(replay.Running);
        Assert.Equal(completed.Report.Id, replay.Report!.Id);
        WorkflowRunStatus conflict = await first.PostAsync<WorkflowRunStatus>("runs.start", request with { EntryNodeId = "target" }).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, conflict.Outcome);
        Assert.Equal("run.requestMismatch", conflict.Reason);
    }
}
