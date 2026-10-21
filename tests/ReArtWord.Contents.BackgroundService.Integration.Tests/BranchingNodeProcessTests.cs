using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>Ifの排他Signalとnull補完を、異なるCore間の実配送で検証します。</summary>
public sealed class BranchingNodeProcessTests
{
    /// <summary>真偽判定を別PCで行い、null時だけ既定値を渡して未選択側を実行しません。</summary>
    /// <returns>保存・型検証・実通信・複数回の押下結果です。</returns>
    [Fact]
    public async Task IfAndDefaultPreserveBranchAndValueAcrossPeersAsync()
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
        WorkflowDefinition definition = new("branching-nodes", "Branching nodes", [
            new("button", "control.button", empty, Target: new(EExecutionScope.One, ["a"])),
            new("if", "control.if", empty, Target: new(EExecutionScope.One, ["b"])),
            new("default", "data.defaultIfNull", empty, new Dictionary<string, NodeValue> { ["defaultValue"] = NodeValue.FromObject("fallback") }),
            new("yes", "control.pass", empty, Target: new(EExecutionScope.One, ["a"])),
            new("no", "control.pass", empty, Target: new(EExecutionScope.One, ["b"]))],
            [new("button", "value", "if", "condition"), new("button", "value2", "default", "value"),
                new("if", "trueStart", "yes", "trigger"), new("if", "falseStart", "no", "trigger"),
                new("default", "value", "yes", "value"), new("default", "value", "no", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        (bool Condition, NodeValue Value, string Expected)[] cases = [(true, NodeValue.Null, "\"fallback\""), (false, NodeValue.FromObject("live"), "\"live\""), (true, NodeValue.FromObject(0), "0")];
        foreach ((bool condition, NodeValue value, string expected) in cases)
        {
            WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision, "button", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(condition), ["value2"] = value });
            WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", request, timeout.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token =>
            {
                run = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
                return !run.Running;
            }, timeout.Token).ConfigureAwait(true);
            Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
            string selected = condition ? "yes"
                                        : "no";
            NodeExecutionReport executed = run.Report!.Nodes.Single(node => string.Equals(node.NodeId, selected, StringComparison.Ordinal));
            TargetNodeResult target = Assert.Single(executed.Targets);
            Assert.Equal(condition ? "a"
                                   : "b", target.PeerId);
            Assert.Equal(expected, target.Result.Outputs["value"].Json.GetRawText());
            Assert.All(run.Report.Nodes.Where(node => node.NodeId is "yes" or "no" && !string.Equals(node.NodeId, selected, StringComparison.Ordinal)),
                node => Assert.Equal(EOutcome.NotExecuted, node.Outcome));
            Assert.Equal("b", Assert.Single(run.Report.Nodes.Single(node => node.NodeId is "if").Targets).PeerId);
        }
    }
}
