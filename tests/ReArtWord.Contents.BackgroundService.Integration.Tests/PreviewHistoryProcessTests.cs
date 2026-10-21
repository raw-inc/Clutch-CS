using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>二つの実Coreへ届く高速なプレビューを、後から取りこぼさず履歴取得します。</summary>
public sealed class PreviewHistoryProcessTests
{
    /// <summary>別PCで抽選した各要求の値を両PCで観測し、最新50件だけ取得できます。</summary>
    /// <returns>実配送、履歴の管理通信、最新本文の整合性です。</returns>
    [Fact]
    public async Task RemoteDrawsReachBothPreviewHistoriesWithoutPollGapsAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition definition = new("preview-history", "Preview history", [
            new("button", "control.button", empty, Target: new(EExecutionScope.One, ["a"])),
            new("draw", "data.randomPick", empty, Target: new(EExecutionScope.One, ["b"])),
            new("preview", "control.preview", empty, Target: new(EExecutionScope.All, ["a", "b"]))],
            [new("button", "value", "draw", "value"), new("draw", "value", "preview", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        for (int index = 0; index < 55; index++)
        {
            WorkflowRunRequest request = new(Guid.NewGuid(), definition.Id, saved.Revision, "button", new Dictionary<string, NodeValue>
            {
                ["value"] = NodeValue.FromObject(new[] { index }),
            });
            WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", request, timeout.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token =>
            {
                run = await first.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
                return !run.Running;
            }, timeout.Token).ConfigureAwait(true);
            Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
        }
        foreach (CorePeer peer in new[] { first, second })
        {
            NodePreviewHistoryEntry[] rows = await peer.GetAsync<NodePreviewHistoryEntry[]>("previews.history", new NodePreviewHistoryQuery(definition.Id, "preview"), timeout.Token).ConfigureAwait(true);
            Assert.Equal(50, rows.Length);
            Assert.Equal(Enumerable.Range(6, 50).Reverse().Select(number => (long)number), rows.Select(row => row.Number));
            Assert.Equal(Enumerable.Range(5, 50).Reverse().Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture)), rows.Select(row => row.Text));
            NodePreviewSnapshot[] latest = await peer.GetAsync<NodePreviewSnapshot[]>("previews.list", new NodePreviewQuery(definition.Id, ["preview"]), timeout.Token).ConfigureAwait(true);
            Assert.Equal(rows[0].Snapshot, Assert.Single(latest));
            Assert.Empty(await peer.GetAsync<NodePreviewHistoryEntry[]>("previews.history", new NodePreviewHistoryQuery("other", "preview"), timeout.Token).ConfigureAwait(true));
        }
    }
}
