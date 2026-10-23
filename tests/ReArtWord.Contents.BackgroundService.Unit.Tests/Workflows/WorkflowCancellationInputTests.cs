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
    /// <summary>リンクしたtokenへの通知が遅れても、呼出元が取り消した後続を開始しません。</summary>
    /// <returns>開始前取消と、実際の失敗・結果不明の優先順です。</returns>
    [Theory]
    [InlineData(EOutcome.NotExecuted, EOutcome.Cancelled)]
    [InlineData(EOutcome.Failed, EOutcome.Failed)]
    [InlineData(EOutcome.Unknown, EOutcome.Unknown)]
    public async Task CancelledInputDoesNotBecomeMissingInput(EOutcome bodyOutcome, EOutcome expected)
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
            [WorkflowFixture.Definition("source", "test.value"), WorkflowFixture.Definition("body", "test.value"), WorkflowFixture.Definition("after", "test.value")],
            [new("source", "value", "body", "value"), new("body", "value", "after", "value"), new("source", CompletionPorts.Signal, "after", "trigger")]);
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(workflow, "source", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject(1),
        }, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        using CancellationTokenRegistration delayed = cancellation.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            resumeCallbacks.Wait(TimeSpan.FromSeconds(10));
        });
        Task cancelling = cancellation.CancelAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            result.TrySetResult(NodeResult.Failure("execution.cancelUnconfirmed", "The operation was cancelled before it started.", bodyOutcome));
            ExecutionReport report = await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.True(report.Outcome == expected, JsonSerializer.Serialize(report));
            Assert.Equal(0, afterCalls);
        }
        finally
        {
            resumeCallbacks.Set();
            await cancelling.ConfigureAwait(true);
        }
    }
}
