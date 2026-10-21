using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Execution.Overlap;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Overlap;

/// <summary>同一ノードの実行場所別受付と未実行境界を確認します。</summary>
public sealed class NodeOverlapTests
{
    /// <summary>既定並行を追跡しつつ別Peerを妨げません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ParallelAndSkipAreScopedToActualPeerAsync()
    {
        NodeOverlapController controller = new();
        TaskCompletionSource<NodeResult> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> first = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Parallel, _ => new(release.Task)).AsTask();
        NodeResult parallel = await controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Parallel, _ => ValueTask.FromResult(NodeResult.Success())).ConfigureAwait(true);
        NodeResult skipped = await controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Skip, _ => throw new InvalidOperationException()).ConfigureAwait(true);
        NodeResult other = await controller.ExecuteAsync("flow", "node", "b", EOverlapPolicy.Skip, _ => ValueTask.FromResult(NodeResult.Success())).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, parallel.Outcome);
        Assert.Equal(EOutcome.NotExecuted, skipped.Outcome);
        Assert.Equal("overlap.running", skipped.ReasonCode);
        Assert.Equal(EOutcome.Succeeded, other.Outcome);
        release.SetResult(NodeResult.Success());
        await first.ConfigureAwait(true);
    }

    /// <summary>最新保留に置換し、旧受付には理由を返します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task LatestReplacesPendingWithoutStartingOldInputAsync()
    {
        NodeOverlapController controller = new();
        TaskCompletionSource<NodeResult> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> first = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Latest, _ => new(release.Task)).AsTask();
        Task<NodeResult> old = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Latest, _ => throw new InvalidOperationException()).AsTask();
        Task<NodeResult> latest = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Latest, _ => ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(2)))).AsTask();
        Assert.Equal("overlap.replaced", (await old.ConfigureAwait(true)).ReasonCode);
        Assert.False(latest.IsCompleted);
        release.SetResult(NodeResult.Success());
        await first.ConfigureAwait(true);
        Assert.Equal(2, (await latest.ConfigureAwait(true)).Outputs["value"].Json.GetInt32());
    }

    /// <summary>順番、有限容量と待機取消を外部結果で確認します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task QueueHasExplicitCapacityAndCancellationAsync()
    {
        NodeOverlapController controller = new(2);
        TaskCompletionSource<NodeResult> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> first = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Queue, _ => new(release.Task)).AsTask();
        using CancellationTokenSource cancel = new();
        Task<NodeResult> cancelled = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Queue, _ => throw new InvalidOperationException(), cancel.Token).AsTask();
        List<int> order = [];
        Task<NodeResult> second = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Queue, _ =>
        {
            order.Add(2);
            return ValueTask.FromResult(NodeResult.Success());
        }).AsTask();
        NodeResult full = await controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Queue, _ => throw new InvalidOperationException()).ConfigureAwait(true);
        Assert.Equal("overlap.capacity", full.ReasonCode);
        await cancel.CancelAsync().ConfigureAwait(true);
        Assert.Equal("overlap.cancelled", (await cancelled.ConfigureAwait(true)).ReasonCode);
        Task<NodeResult> third = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Queue, _ =>
        {
            order.Add(3);
            return ValueTask.FromResult(NodeResult.Success());
        }).AsTask();
        release.SetResult(NodeResult.Success());
        await Task.WhenAll(first, second, third).ConfigureAwait(true);
        Assert.Equal([2, 3], order);
    }

    /// <summary>複数の並行実行がすべて終わるまで順番待ちを開始しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ChangedPolicyWaitsForAllPriorParallelExecutionsAsync()
    {
        NodeOverlapController controller = new();
        TaskCompletionSource<NodeResult> firstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NodeResult> secondRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> first = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Parallel, _ => new(firstRelease.Task)).AsTask();
        Task<NodeResult> second = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Parallel, _ => new(secondRelease.Task)).AsTask();
        Task<NodeResult> queued = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Queue, _ => ValueTask.FromResult(NodeResult.Success())).AsTask();
        firstRelease.SetResult(NodeResult.Success());
        await first.ConfigureAwait(true);
        Assert.False(queued.IsCompleted);
        secondRelease.SetResult(NodeResult.Success());
        await Task.WhenAll(second, queued).ConfigureAwait(true);
    }

    /// <summary>実処理例外でも次回の実行枠を解放します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task AdapterExceptionDoesNotBlockNextExecutionAsync()
    {
        NodeOverlapController controller = new();
        await Assert.ThrowsAsync<IOException>(() => controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Queue, _ => throw new IOException("adapter failure")).AsTask()).ConfigureAwait(true);
        NodeResult next = await controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Skip, _ => ValueTask.FromResult(NodeResult.Success())).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, next.Outcome);
    }

    /// <summary>開始済の取消によって確認済の実ノード結果を上書きしません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CancellationAfterStartPreservesActualResultAsync()
    {
        NodeOverlapController controller = new();
        using CancellationTokenSource cancel = new();
        TaskCompletionSource<NodeResult> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NodeResult> first = controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Queue, _ => new(release.Task), cancel.Token).AsTask();
        await cancel.CancelAsync().ConfigureAwait(true);
        release.SetResult(NodeResult.Failure("wire.uncertain", "不明", EOutcome.Unknown));
        Assert.Equal(EOutcome.Unknown, (await first.ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await controller.ExecuteAsync("flow", "node", "a", EOverlapPolicy.Skip, _ => ValueTask.FromResult(NodeResult.Success())).ConfigureAwait(true)).Outcome);
    }
}
