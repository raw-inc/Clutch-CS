using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>入れ子の制御信号が別の反復・要素へ漏れず、外側の停止も未開始処理へ伝わることを確認します。</summary>
public sealed class NestedIterationControlTests
{
    /// <summary>内側のbreakはそのループだけを終了し、外側では次の要素を通常どおり処理します。</summary>
    /// <returns>外側二回・内側一回ずつの結果です。</returns>
    [Fact]
    public async Task InnerBreakDoesNotTerminateTheOuterLoopAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = IterationControlTests.Create((context, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(IterationControlTestNode.Result(context.Inputs["value"].Json.GetInt32()));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition source = Nested();
        WorkflowDefinition document = source with { Wires = [.. source.Wires, new("body", CompletionPorts.Signal, "inner-end", "break")] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, document)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, System.Text.Json.JsonSerializer.Serialize(report));
        Assert.Equal(2, calls);
        Assert.Equal("[[10],[10]]", IterationResultTests.Outputs(report)["value"].Json.GetRawText());
        Assert.Equal(2, IterationTests.Loop(report).Iteration!.Started);
    }

    /// <summary>外側のbreak後は、開始済み内側処理を待ち、内側の次の要素も開始しません。</summary>
    /// <returns>外側・内側とも一回だけ開始した確認です。</returns>
    [Fact]
    public async Task OuterBreakStopsUnstartedNestedElementsWithoutAbandoningStartedWorkAsync()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource controlled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<int> values = new();
        WorkflowFixture fixture = IterationControlTests.Create(async (context, token) =>
        {
            int value = context.Inputs["value"].Json.GetInt32();
            string body = "body";
            if (string.Equals(context.NodeId, body, StringComparison.Ordinal))
            {
                values.Enqueue(value);
                entered.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            else
            {
                await entered.Task.WaitAsync(token).ConfigureAwait(false);
            }
            return IterationControlTestNode.Result(value);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        IterationControlTests.ObserveControl(fixture, controlled);
        WorkflowDefinition source = Nested();
        WorkflowDefinition document = source with
        {
            Nodes = [.. source.Nodes, WorkflowFixture.Definition("stop", "test.iteration")],
            Wires = [.. source.Wires, new("each", "item", "stop", "value"), new("stop", CompletionPorts.Signal, "end", "break")],
        };
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, document), cancellationToken: cancellation.Token);
        try
        {
            await controlled.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.False(running.IsCompleted);
            Assert.Equal([10], values);
            release.TrySetResult();
            ExecutionReport report = await running.ConfigureAwait(true);
            Assert.True(report.Outcome == EOutcome.Succeeded, System.Text.Json.JsonSerializer.Serialize(report));
            Assert.Equal([10], values);
            Assert.Equal(1, IterationTests.Loop(report).Iteration!.Started);
            Assert.Equal("[]", IterationResultTests.Outputs(report)["value"].Json.GetRawText());
        }
        finally
        {
            release.TrySetResult();
            await cancellation.CancelAsync().ConfigureAwait(true);
            await running.ConfigureAwait(true);
        }
    }

    /// <summary>内側の結果だけを外側へ渡す二重の有限反復を作ります。</summary>
    /// <returns>それぞれ固有のEndを持つ定義です。</returns>
    private static WorkflowDefinition Nested()
    {
        NodeDefinition outer = WorkflowFixture.Definition("each", IterationNodeTypes.ForEach) with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromJson(NodeSettings.Schema("[1,2]")) } };
        NodeDefinition inner = WorkflowFixture.Definition("inner", IterationNodeTypes.ForEach) with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromJson(NodeSettings.Schema("[10,20,30]")) } };
        return new("nested-control", "Nested control", [outer, inner, WorkflowFixture.Definition("body", "test.iteration"), WorkflowFixture.Definition("inner-end", IterationNodeTypes.End), WorkflowFixture.Definition("end", IterationNodeTypes.End)],
            [new("each", "start", "inner", "trigger"), new("inner", "item", "body", "value"), new("inner", "loop", "inner-end", "loop"),
                new("body", "value", "inner-end", "value"), new("each", "loop", "end", "loop"), new("inner-end", "value", "end", "value")]);
    }
}
