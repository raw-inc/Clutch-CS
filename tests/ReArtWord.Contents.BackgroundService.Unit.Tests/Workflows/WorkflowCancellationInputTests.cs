using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>取消通知の伝播と上流結果の到着が競合しても、入力不足へ読み替えないことを確認します。</summary>
public sealed class WorkflowCancellationInputTests
{
    /// <summary>取消通知が伝わる途中でも、後続の有無によらず確認済みの結果を集約します。</summary>
    /// <returns>開始前取消と、実際の成功・失敗・結果不明の優先順です。</returns>
    [Theory]
    [InlineData(true, EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(true, EOutcome.Failed, EOutcome.Failed)]
    [InlineData(true, EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData(true, EOutcome.Succeeded, EOutcome.Cancelled)]
    [InlineData(false, EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(false, EOutcome.Failed, EOutcome.Failed)]
    [InlineData(false, EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData(false, EOutcome.Succeeded, EOutcome.Succeeded)]
    public async Task CancellationResultsDoNotDependOnLinkedNotification(bool hasDownstream, EOutcome bodyOutcome, EOutcome expected)
    {
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim resumeCallbacks = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NodeResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int afterCalls = 0;
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            const string after = "after";
            if (string.Equals(context.NodeId, after, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref afterCalls);
            }
            return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
        });
        WorkflowFixture fixture = new(probe);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            const string body = "body";
            if (string.Equals(request.Node.Id, body, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                return await result.Task.ConfigureAwait(false);
            }
            return await fixture.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
        };
        WorkflowDefinition definition = new("cancelled-input", "Cancelled input",
            [WorkflowFixture.Definition("source", "test.value"), WorkflowFixture.Definition("body", "test.value")],
            [new("source", "value", "body", "value")]);
        if (hasDownstream)
        {
            definition = definition with
            {
                Nodes = [.. definition.Nodes, WorkflowFixture.Definition("after", "test.value")],
                Wires = [.. definition.Wires, new("body", "value", "after", "value"), new("source", CompletionPorts.Signal, "after", "trigger")],
            };
        }
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(workflow, "source", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject(1),
        }, cancellationToken: cancellation.Token);
        CancellationTokenRegistration delayed = default;
        Task cancelling = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            delayed = cancellation.Token.Register(() =>
            {
                callbackEntered.TrySetResult();
                resumeCallbacks.Wait();
            });
            cancelling = cancellation.CancelAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            NodeResult bodyResult = bodyOutcome == EOutcome.Succeeded ? ValueMergeFixture.Result(NodeValue.FromObject(1))
                                                                      : NodeResult.Failure("execution.cancelUnconfirmed", "The operation was cancelled before it started.", bodyOutcome);
            result.TrySetResult(bodyResult);
            ExecutionReport report = await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.False(cancelling.IsCompleted);
            Assert.True(report.Outcome == expected, JsonSerializer.Serialize(report));
            Assert.Equal(0, afterCalls);
            Assert.Equal(bodyOutcome, Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "body", StringComparison.Ordinal)).Outcome);
        }
        finally
        {
            resumeCallbacks.Set();
            result.TrySetResult(NodeResult.Failure("fixture.cleanup", "The test ended.", EOutcome.Cancelled));
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(true);
            }
            finally
            {
                try
                {
                    await Task.WhenAll(cancelling, running).ConfigureAwait(true);
                }
                finally
                {
                    await delayed.DisposeAsync().ConfigureAwait(true);
                }
            }
        }
    }

    /// <summary>反復の結果判定と次要素の受付にも、通知途中の呼出元の取消を適用します。</summary>
    /// <returns>最初の要素の成否を保ち、残りの要素を開始しない確認です。</returns>
    [Theory]
    [InlineData(EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(EOutcome.Succeeded, EOutcome.Cancelled)]
    [InlineData(EOutcome.Failed, EOutcome.Failed)]
    [InlineData(EOutcome.Unknown, EOutcome.Unknown)]
    public async Task IterationCancellationDoesNotWaitForLinkedNotification(EOutcome bodyOutcome, EOutcome expected)
    {
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim resumeCallbacks = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<NodeResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = IterationTests.Create((context, _) => ValueTask.FromResult(NodeResult.Success(context.Inputs["value"])));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            const string body = "body";
            if (string.Equals(request.Node.Id, body, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                return await result.Task.ConfigureAwait(false);
            }
            return await fixture.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
        };
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, IterationTests.Definition(end: false)), cancellationToken: cancellation.Token);
        CancellationTokenRegistration delayed = default;
        Task cancelling = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            delayed = cancellation.Token.Register(() =>
            {
                callbackEntered.TrySetResult();
                resumeCallbacks.Wait();
            });
            cancelling = cancellation.CancelAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            NodeResult bodyResult = bodyOutcome == EOutcome.Succeeded ? NodeResult.Success(NodeValue.FromObject(1))
                                                                      : NodeResult.Failure("execution.cancelUnconfirmed", "The operation did not complete.", bodyOutcome);
            result.TrySetResult(bodyResult);
            ExecutionReport report = await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.False(cancelling.IsCompleted);
            Assert.True(report.Outcome == expected, JsonSerializer.Serialize(report));
            Assert.Equal(1, IterationTests.Loop(report).Iteration!.Started);
            Assert.Empty(fixture.Engine.Active());
        }
        finally
        {
            resumeCallbacks.Set();
            result.TrySetResult(NodeResult.Failure("fixture.cleanup", "The test ended.", EOutcome.Cancelled));
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(true);
            }
            finally
            {
                try
                {
                    await Task.WhenAll(cancelling, running).ConfigureAwait(true);
                }
                finally
                {
                    await delayed.DisposeAsync().ConfigureAwait(true);
                }
            }
        }
    }
}
