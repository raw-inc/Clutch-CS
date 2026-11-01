using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>有限処理の取消を別Coreへ送り、独立した別実行に影響しないことを確認します。</summary>
public sealed class WorkflowCancellationTests
{
    /// <summary>日時Sourceが完了した後も、下流の一件を一覧から選んで取り消せます。</summary>
    /// <returns>実行一覧、元のSource状態、取消後の実行結果の確認。</returns>
    [Fact]
    public async Task CompletedSourceRetainsCancellableDownstreamRunAsync()
    {
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null).ConfigureAwait(true);
        WorkflowDefinition definition = new("source-run", "Source run", [
            new("timer", "scheduling.timer", JsonSerializer.SerializeToElement(new { mode = "once", at = DateTimeOffset.UtcNow.AddSeconds(1), missed = "delay" }), StartOnLoad: true),
            new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 60000 })),
        ], [new("timer", "value", "delay", "trigger")], Enabled: true);
        WorkflowSaveResult saved = await peer.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("runningNodes").GetArrayLength() == 1 && status.GetProperty("sources").EnumerateArray()
                .Any(item => string.Equals(item.GetProperty("status").GetProperty("state").GetString(), "completed", StringComparison.Ordinal));
        }).ConfigureAwait(true);
        WorkflowRunStatus run = Assert.Single((await peer.GetAsync<ActiveRunsPage>("runs.active").ConfigureAwait(true)).Items);
        Assert.Equal(definition.Id, run.WorkflowId);
        WorkflowCancelResult cancelled = await peer.PostAsync<WorkflowCancelResult>("runs.cancel", new WorkflowCancelRequest(Guid.NewGuid(), run.OperationId, run.OwnerInstanceId!.Value)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, cancelled.Outcome);
        Assert.True(cancelled.Requested);
        WorkflowRunStatus finished = run;
        await CorePeer.WaitAsync(async token =>
        {
            finished = await peer.GetAsync<WorkflowRunStatus>("runs.read", run.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !finished.Running && finished.Outcome != EOutcome.Unknown;
        }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Cancelled, finished.Outcome);
        Assert.Empty((await peer.GetAsync<ActiveRunsPage>("runs.active").ConfigureAwait(true)).Items);
        JsonElement current = await peer.GetAsync<JsonElement>("status").ConfigureAwait(true);
        Assert.All(current.GetProperty("sources").EnumerateArray(), item => Assert.Equal("completed", item.GetProperty("status").GetProperty("state").GetString()));
    }

    /// <summary>元の実体へ一件だけの取消を送り、結果照会・重複要求・古い実体の拒否を確認します。</summary>
    /// <returns>実Core二台間での実行と取消の確認。</returns>
    [Fact]
    public async Task CancellationTargetsOnlyTheOriginalRunAcrossPeersAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2
                && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null;
        }).ConfigureAwait(true);
        WorkflowRunStatus firstRun = await StartAsync(first, "first").ConfigureAwait(true);
        WorkflowRunStatus otherRun = await StartAsync(first, "other").ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("runningNodes").GetArrayLength() == 2).ConfigureAwait(true);
        ActiveRunsPage page = await first.GetAsync<ActiveRunsPage>("runs.active", new ActiveRunsQuery(1)).ConfigureAwait(true);
        Assert.Single(page.Items);
        Assert.NotNull(page.Next);
        ActiveRunsPage next = await first.GetAsync<ActiveRunsPage>("runs.active", new ActiveRunsQuery(1, page.Next)).ConfigureAwait(true);
        Assert.Single(next.Items);
        Assert.NotEqual(page.Items[0].OperationId, next.Items[0].OperationId);
        Assert.Null(next.Next);
        WorkflowCancelResult refused = await first.PostAsync<WorkflowCancelResult>("runs.cancel",
            new WorkflowCancelRequest(Guid.NewGuid(), otherRun.OperationId, Guid.NewGuid())).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, refused.Outcome);
        Assert.False(refused.Requested);
        WorkflowCancelRequest cancellation = new(Guid.NewGuid(), firstRun.OperationId, firstRun.OwnerInstanceId!.Value);
        WorkflowCancelResult accepted = await first.PostAsync<WorkflowCancelResult>("runs.cancel", cancellation).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, accepted.Outcome);
        Assert.True(accepted.Requested);
        WorkflowRunStatus finished = firstRun;
        await CorePeer.WaitAsync(async token =>
        {
            finished = await first.GetAsync<WorkflowRunStatus>("runs.read", firstRun.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !finished.Running;
        }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Cancelled, finished.Outcome);
        Assert.True((await first.GetAsync<WorkflowRunStatus>("runs.read", otherRun.OperationId.ToString("D")).ConfigureAwait(true)).Running);
        Assert.Equal(1, (await second.GetAsync<JsonElement>("status").ConfigureAwait(true)).GetProperty("runningNodes").GetArrayLength());
        Assert.Equal(accepted, await first.GetAsync<WorkflowCancelResult>("runs.cancellation", cancellation.OperationId.ToString("D")).ConfigureAwait(true));
        Assert.Equal(accepted, await first.PostAsync<WorkflowCancelResult>("runs.cancel", cancellation).ConfigureAwait(true));
        WorkflowCancelResult mismatch = await first.PostAsync<WorkflowCancelResult>("runs.cancel", cancellation with { RunId = otherRun.OperationId }).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, mismatch.Outcome);
        Assert.Equal("run.cancelRequestMismatch", mismatch.Reason);
        _ = await first.PostAsync<WorkflowCancelResult>("runs.cancel", new WorkflowCancelRequest(Guid.NewGuid(), otherRun.OperationId, otherRun.OwnerInstanceId!.Value)).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => !(await first.GetAsync<WorkflowRunStatus>("runs.read", otherRun.OperationId.ToString("D"), token).ConfigureAwait(true)).Running).ConfigureAwait(true);
        Assert.Empty((await first.GetAsync<ActiveRunsPage>("runs.active").ConfigureAwait(true)).Items);
    }

    /// <summary>保存済みワークフローの長い待機を別Peerで実行します。</summary>
    /// <returns>元の実行IDと開始元の実体。</returns>
    private static async Task<WorkflowRunStatus> StartAsync(CorePeer peer, string id)
    {
        WorkflowDefinition definition = new(id, id, [new NodeDefinition("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 60000 }),
            Target: new TargetDefinition(EExecutionScope.One, ["b"]))], []);
        WorkflowSaveResult saved = await peer.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunStatus status = await peer.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.NewGuid(), definition.Id)).ConfigureAwait(true);
        Assert.True(status.Running);
        return status;
    }
}
