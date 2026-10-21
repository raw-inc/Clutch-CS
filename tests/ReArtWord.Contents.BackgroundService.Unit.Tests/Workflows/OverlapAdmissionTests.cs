using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>遠隔受付と手動実行が共通の重複制御を通ることを確認します。</summary>
public sealed class OverlapAdmissionTests
{
    /// <summary>重複待ち後の安全待機中に更新保留が閉じても、新しい副作用を開始しません。</summary>
    /// <returns>安全待機解除直後のadmission再確認です。</returns>
    [Fact]
    public async Task MaintenanceClosedDuringFinalSafetyWaitPreventsStarting()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource heldAtSafety = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int stopped = 0;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(async (_, cancellation) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellation).ConfigureAwait(true);
            return NodeResult.Success();
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        fixture.Safety.ManualStopCheck = (_, _) =>
        {
            if (Volatile.Read(ref stopped) != 0)
            {
                heldAtSafety.TrySetResult();
                return true;
            }
            return false;
        };
        NodeDefinition node = WorkflowFixture.Definition("n") with { Overlap = EOverlapPolicy.Queue, Safety = new(StopWhenSingleton: false, Inputs: EStoppedInputPolicy.All) };
        Task<NodeResult> first = fixture.Executor.ExecuteAsync(Request(node, "first", fixture), timeout.Token);
        await started.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        Task<NodeResult> queued = fixture.Executor.ExecuteAsync(Request(node, "queued", fixture), timeout.Token);
        Volatile.Write(ref stopped, 1);
        release.SetResult();
        await heldAtSafety.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        fixture.Executor.AdmissionCheck = static () => "update.hold";
        Volatile.Write(ref stopped, 0);
        Assert.Equal(EOutcome.Succeeded, (await first.ConfigureAwait(true)).Outcome);
        NodeResult rejected = await queued.ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, rejected.Outcome);
        Assert.Equal("execution.maintenance", rejected.ReasonCode);
        Assert.Equal(1, calls);
    }

    /// <summary>保留中の設定変更が、既に受け付けた入力を書き換えません。</summary>
    /// <returns>実行境界の順番と設定固定の確認です。</returns>
    [Fact]
    public async Task PendingActionUsesCapturedInputsAndMaintenanceRefusesBeforeStart()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string> values = [];
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(async (context, cancellation) =>
        {
            values.Enqueue(context.Inputs["value"].Json.GetString()!);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellation).ConfigureAwait(true);
            return NodeResult.Success();
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        NodeDefinition node = WorkflowFixture.Definition("n") with { Overlap = EOverlapPolicy.Queue };
        RemoteExecutionRequest first = Request(node, "first", fixture);
        Task<NodeResult> active = fixture.Executor.ExecuteAsync(first, timeout.Token);
        await started.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        Task<NodeResult> pending = fixture.Executor.ExecuteAsync(Request(node, "captured", fixture), timeout.Token);
        Assert.False(pending.IsCompleted);
        fixture.Executor.AdmissionCheck = static () => "maintenance";
        release.SetResult();

        Assert.Equal(EOutcome.Succeeded, (await active.ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await pending.ConfigureAwait(true)).Outcome);
        Assert.Equal(["first"], values);
        fixture.Executor.AdmissionCheck = null;
        Assert.Equal(EOutcome.Succeeded, (await fixture.Executor.ExecuteAsync(Request(node, "new", fixture), timeout.Token).ConfigureAwait(true)).Outcome);
        Assert.Equal(["first", "new"], values);
    }

    /// <summary>実体を指定した独立した実行要求を作ります。</summary>
    /// <returns>固定した設定と入力を持つ要求です。</returns>
    private static RemoteExecutionRequest Request(NodeDefinition node, string value, WorkflowFixture fixture) => new(Guid.NewGuid(), Guid.NewGuid(), "w", "revision", node,
        new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) }, fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
}
