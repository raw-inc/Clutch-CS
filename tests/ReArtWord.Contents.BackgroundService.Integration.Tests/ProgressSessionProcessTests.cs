using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Core二台で、並走WFと並列反復のProgressを分離して終了時に回収します。</summary>
public sealed class ProgressSessionProcessTests
{
    /// <summary>同じProgressへ二実行から到着しても混ざらず、一方の取消を他方へ広げません。</summary>
    /// <returns>既定集約・要素分離・遠隔表示・取消・正常終了の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRunsAndIterationsKeepIndependentProgressAsync(bool separate)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        string grouping = separate ? "iteration"
                                   : "workflow";
        WorkflowDefinition definition = new("progress-sessions", "Concurrent progress", [
            new("button", "control.button", empty, Target: new(EExecutionScope.One, ["a"])),
            new("each", IterationNodeTypes.ParallelForEach, JsonSerializer.SerializeToElement(new { maxConcurrency = 3 }), Target: new(EExecutionScope.One, ["a"])),
            new("progress", "control.progress", separate ? JsonSerializer.SerializeToElement(new { progressScope = grouping })
                                                         : empty, Target: new(EExecutionScope.One, ["b"])),
            new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 15000 }), Target: new(EExecutionScope.One, ["b"])),
            new("end", IterationNodeTypes.End, empty, Target: new(EExecutionScope.One, ["a"]))],
            [new("button", "value", "each", "value"), new("each", "item", "progress", "current"), new("each", "count", "progress", "total"),
             new("each", "item", "delay", "value"), new("each", "loop", "end", "loop"), new("delay", "value", "end", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        WorkflowRunStatus one = await StartAsync(first, definition.Id, saved.Revision!, Enumerable.Range(1, 3).ToArray(), timeout.Token).ConfigureAwait(true);
        WorkflowRunStatus two = await StartAsync(first, definition.Id, saved.Revision!, Enumerable.Range(11, 3).ToArray(), timeout.Token).ConfigureAwait(true);
        NodePreviewSnapshot[] visible = [];
        int perRun = separate ? 3
                              : 1;
        await CorePeer.WaitAsync(async token =>
        {
            visible = await second.GetAsync<NodePreviewSnapshot[]>("previews.list", new NodePreviewQuery(definition.Id, ["progress"]), token).ConfigureAwait(false);
            return visible.Length == perRun * 2;
        }, timeout.Token).ConfigureAwait(true);
        Assert.All(visible.Where(item => item.Session!.RunId == one.OperationId), item => Assert.InRange(item.Progress!.Current, 1, 3));
        Assert.All(visible.Where(item => item.Session!.RunId == two.OperationId), item => Assert.InRange(item.Progress!.Current, 11, 13));
        Assert.Equal(perRun, visible.Count(item => item.Session!.RunId == one.OperationId));
        Assert.Equal(perRun, visible.Count(item => item.Session!.RunId == two.OperationId));
        Assert.All(visible, item => Assert.Equal(separate, item.Session!.ScopeId != Guid.Empty));
        WorkflowCancelResult cancellation = await first.PostAsync<WorkflowCancelResult>("runs.cancel", new WorkflowCancelRequest(Guid.NewGuid(), one.OperationId, one.OwnerInstanceId!.Value), timeout.Token).ConfigureAwait(true);
        Assert.True(cancellation.Requested);
        await CorePeer.WaitAsync(async token =>
        {
            visible = await second.GetAsync<NodePreviewSnapshot[]>("previews.list", new NodePreviewQuery(definition.Id, ["progress"]), token).ConfigureAwait(false);
            return visible.Length == perRun && visible.All(item => item.Session!.RunId == two.OperationId);
        }, timeout.Token).ConfigureAwait(true);
        WorkflowRunStatus completed = two;
        await CorePeer.WaitAsync(async token =>
        {
            completed = await first.GetAsync<WorkflowRunStatus>("runs.read", two.OperationId.ToString("D"), token).ConfigureAwait(false);
            return !completed.Running;
        }, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, completed.Outcome);
        Assert.Empty(await second.GetAsync<NodePreviewSnapshot[]>("previews.list", new NodePreviewQuery(definition.Id, ["progress"]), timeout.Token).ConfigureAwait(true));
    }

    /// <summary>同じ定義に、独立した入力と元要求IDで一回だけ開始します。</summary>
    /// <returns>開始した実行の所有情報です。</returns>
    private static Task<WorkflowRunStatus> StartAsync(CorePeer peer, string workflow, string revision, int[] values, CancellationToken cancellationToken = default)
        => peer.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.NewGuid(), workflow, revision, "button",
            new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(values) }), cancellationToken);
}
