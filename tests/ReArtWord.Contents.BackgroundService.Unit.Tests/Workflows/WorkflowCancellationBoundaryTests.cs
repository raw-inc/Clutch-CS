using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>区間終了と合流でも、通知順序によって取消を別の結果へ変えないことを確認します。</summary>
public sealed class WorkflowCancellationBoundaryTests
{
    /// <summary>最後の処理の取消を、同時実行制限と値合流の結果へ反映します。</summary>
    /// <returns>取消理由・実際の成否の保持と、枠および子実行の回収です。</returns>
    [Theory]
    [InlineData("limit", true, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("limit", true, "execution.cancelledBeforeStart", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("limit", true, "overlap.cancelled", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("limit", true, "source.cancelledBeforeExecution", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("limit", false, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData("limit", true, "fixture.blocked", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData("limit", true, "fixture.failed", EOutcome.Failed, EOutcome.Failed)]
    [InlineData("limit", true, "execution.cancelUnconfirmed", EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData("limit", true, "", EOutcome.Succeeded, EOutcome.Succeeded)]
    [InlineData("value", true, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("value", true, "execution.cancelledBeforeStart", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("value", true, "overlap.cancelled", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("value", true, "source.cancelledBeforeExecution", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("value", false, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData("value", true, "fixture.blocked", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData("value", true, "fixture.failed", EOutcome.Failed, EOutcome.Failed)]
    [InlineData("value", true, "execution.cancelUnconfirmed", EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData("value", true, "", EOutcome.Succeeded, EOutcome.Cancelled)]
    [InlineData("signal", true, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("signal", true, "execution.cancelledBeforeStart", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("signal", true, "overlap.cancelled", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("signal", true, "source.cancelledBeforeExecution", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData("signal", false, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData("signal", true, "fixture.blocked", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData("signal", true, "fixture.failed", EOutcome.Failed, EOutcome.Failed)]
    [InlineData("signal", true, "execution.cancelUnconfirmed", EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData("signal", true, "", EOutcome.Succeeded, EOutcome.Cancelled)]
    public async Task BoundariesPreserveCancellationWhileLinkedNotificationIsPending(string boundary, bool cancel, string reason, EOutcome bodyOutcome, EOutcome expected)
    {
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim resumeCallbacks = new(false);
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NodeResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int afterCalls = 0;
        const string limit = "limit", signal = "signal";
        bool limited = string.Equals(boundary, limit, StringComparison.Ordinal);
        WorkflowFixture fixture;
        WorkflowDefinition definition;
        if (limited)
        {
            fixture = ConcurrencyWorkflowTests.Create((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"])), limiter);
            definition = ConcurrencyWorkflowTests.Definition(false);
        }
        else
        {
            ReArtWord.Contents.BackgroundService.Runtime.Nodes.INode merge = new ValueMergeNode();
            if (string.Equals(boundary, signal, StringComparison.Ordinal))
            {
                merge = new SignalMergeNode("each", new TestStateStore());
            }
            fixture = new(merge, new ValueMergeFixture.Probe((context, _) =>
            {
                const string after = "after";
                if (string.Equals(context.NodeId, after, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref afterCalls);
                }
                return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
            }));
            definition = Merge(merge.Descriptor.Type, string.Equals(boundary, signal, StringComparison.Ordinal));
        }
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        string blocked = limited ? "body"
                                 : "merge";
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            if (string.Equals(request.Node.Id, blocked, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                return await result.Task.ConfigureAwait(false);
            }
            return await fixture.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
        };
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition), cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        using CancellationTokenRegistration delayed = cancellation.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            Assert.True(resumeCallbacks.Wait(TimeSpan.FromSeconds(10)), "The cancellation gate timed out before the test released it.");
        });
        Task cancelling = Task.CompletedTask;
        try
        {
            if (cancel)
            {
                cancelling = cancellation.CancelAsync();
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            }
            NodeResult bodyResult;
            if (bodyOutcome == EOutcome.Succeeded)
            {
                bodyResult = new(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                {
                    ["value"] = NodeValue.FromObject(1), ["start"] = NodeValue.FromObject(true),
                });
            }
            else
            {
                bodyResult = NodeResult.Failure(reason, "Fixture operation did not complete.", bodyOutcome);
            }
            result.TrySetResult(bodyResult);
            ExecutionReport report = await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.True(report.Outcome == expected, JsonSerializer.Serialize(report));
            Assert.Equal(0, afterCalls);
            Assert.Equal(0, limiter.Active);
            Assert.Equal(Convert.ToInt32(limited), limiter.Released);
            Assert.Empty(fixture.Engine.Active());
        }
        finally
        {
            resumeCallbacks.Set();
            await cancelling.ConfigureAwait(true);
        }
    }

    /// <summary>複数PCの成功と未実行が混在しても、取消とその他の未完了を区別します。</summary>
    /// <returns>対象別結果の保持と、要求全体の成否です。</returns>
    [Theory]
    [InlineData(true, false, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(true, false, "execution.cancelledBeforeStart", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(true, false, "overlap.cancelled", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(true, false, "source.cancelledBeforeExecution", EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(false, false, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(true, true, "execution.cancelUnconfirmed", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(true, false, "fixture.blocked", EOutcome.NotExecuted, EOutcome.Failed)]
    [InlineData(true, false, "fixture.failed", EOutcome.Failed, EOutcome.Failed)]
    [InlineData(true, false, "execution.unconfirmed", EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData(true, false, "fixture.cancelled", EOutcome.Cancelled, EOutcome.Cancelled)]
    [InlineData(true, false, "", EOutcome.Succeeded, EOutcome.Succeeded)]
    public async Task PartialTargetCancellationPreservesConfirmedPeerResults(bool cancel, bool rejectedPeer, string reason, EOutcome bodyOutcome, EOutcome expected)
    {
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim resumeCallbacks = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NodeResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(static (_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Peers.Others =
        [
            new PeerInfo("b", Guid.NewGuid(), "host-b", "127.0.0.2", 0, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow),
            new PeerInfo("c", Guid.NewGuid(), "host-c", "127.0.0.3", 0, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow),
        ];
        fixture.Dispatcher.Handler = async (request, _) =>
        {
            const string delayedPeer = "b", otherPeer = "c";
            if (string.Equals(request.TargetPeerId, delayedPeer, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                return await result.Task.ConfigureAwait(false);
            }
            if (rejectedPeer && string.Equals(request.TargetPeerId, otherPeer, StringComparison.Ordinal))
            {
                return NodeResult.Failure("fixture.blocked", "The other peer could not start.", EOutcome.NotExecuted);
            }
            return NodeResult.Success(NodeValue.FromObject(1));
        };
        NodeDefinition node = WorkflowFixture.Definition("action") with { Target = new TargetDefinition(EExecutionScope.All, ["a", "b", "c"]) };
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, new("cancel-targets", "Cancel targets", [node], [])), cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        using CancellationTokenRegistration delayed = cancellation.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            Assert.True(resumeCallbacks.Wait(TimeSpan.FromSeconds(10)), "The cancellation gate timed out before the test released it.");
        });
        Task cancelling = Task.CompletedTask;
        try
        {
            if (cancel)
            {
                cancelling = cancellation.CancelAsync();
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            }
            NodeResult confirmed = NodeResult.Success(NodeValue.FromObject(1));
            if (bodyOutcome != EOutcome.Succeeded)
            {
                confirmed = NodeResult.Failure(reason, "Fixture operation did not complete.", bodyOutcome);
            }
            result.TrySetResult(confirmed);
            ExecutionReport report = await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.True(report.Outcome == expected, JsonSerializer.Serialize(report));
            IReadOnlyList<TargetNodeResult> targets = Assert.Single(report.Nodes).Targets;
            const string succeededPeer = "a", cancelledPeer = "b";
            Assert.Equal(EOutcome.Succeeded, Assert.Single(targets, target => string.Equals(target.PeerId, succeededPeer, StringComparison.Ordinal)).Result.Outcome);
            Assert.Equal(confirmed, Assert.Single(targets, target => string.Equals(target.PeerId, cancelledPeer, StringComparison.Ordinal)).Result);
            Assert.Equal(3, targets.Count);
            Assert.Empty(fixture.Engine.Active());
        }
        finally
        {
            resumeCallbacks.Set();
            await cancelling.ConfigureAwait(true);
        }
    }

    /// <summary>値合流の到着から、別実行の後続を開始する定義です。</summary>
    /// <returns>先行処理、合流、未開始の後続です。</returns>
    private static WorkflowDefinition Merge(string type, bool signal)
    {
        List<WireDefinition> wires = [new("source", "value", "merge", "input1"), new("merge", "start", "after", "trigger"), new("merge", "value", "after", "value")];
        if (signal)
        {
            wires = [new("source", CompletionPorts.Signal, "merge", "input1"), new("merge", "start", "after", "trigger")];
        }
        return new("cancel-merge-boundary", "Cancel merge boundary",
        [WorkflowFixture.Definition("source", "test.value") with
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(1) },
        }, WorkflowFixture.Definition("merge", type), WorkflowFixture.Definition("after", "test.value")], wires);
    }
}
