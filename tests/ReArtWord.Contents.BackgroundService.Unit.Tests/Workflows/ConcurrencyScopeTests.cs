using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>反復・合流・取消を含む区間の所有を実グラフで確認します。</summary>
public sealed class ConcurrencyScopeTests
{
    /// <summary>並列反復の中でも値を混ぜずに枠を制限します。</summary>
    /// <returns>要素ごとの到達と入力順の集約です。</returns>
    [Fact]
    public async Task GateInsideParallelIterationPreservesEachValueAsync()
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        ConcurrentBag<int> observed = [];
        WorkflowFixture fixture = new(new ForEachNode(true), new ForEachEndNode(), new ConcurrencyLimitNode(), new ConcurrencyLimitNode(true),
            new WorkflowFixture.ActionNode(async (context, token) =>
            {
                Assert.Equal(1, limiter.Active);
                int value = context.Inputs["value"].Json.GetInt32();
                await Task.Delay(10, token).ConfigureAwait(false);
                observed.Add(value);
                return NodeResult.Success(context.Inputs["value"]);
            }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.ConcurrencyLimiter = limiter;
        WorkflowDefinition inside = ConcurrencyWorkflowTests.Definition(true);
        WorkflowDefinition definition = inside with
        {
            Nodes = [WorkflowFixture.Definition("each", IterationNodeTypes.ParallelForEach, "{\"maxConcurrency\":3}") with
            {
                Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromJson(NodeSettings.Schema("[1,2,3]")) },
            }, .. inside.Nodes.Select(node => node with { Inputs = null }), WorkflowFixture.Definition("each-end", IterationNodeTypes.End)],
            Wires = [.. inside.Wires, new("each", "item", "limit", "value"), new("each", "loop", "each-end", "loop"), new("end", "value", "each-end", "value")],
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal([1, 2, 3], observed.Order());
        Assert.Equal("[1,2,3]", report.Nodes.Single(node => node.NodeId is "each-end").Targets.Single().Result.Outputs["value"].Json.GetRawText());
        Assert.Equal(3, limiter.Released);
    }

    /// <summary>独立した値合流へ発火した子が完了するまで枠を返しません。</summary>
    /// <returns>子実行を含む区間寿命です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded)]
    [InlineData(EOutcome.Failed)]
    [InlineData(EOutcome.Cancelled)]
    [InlineData(EOutcome.Unknown)]
    public async Task OmittedEndWaitsForValueMergeChildAsync(EOutcome outcome)
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        int calls = 0;
        WorkflowFixture fixture = new(new ConcurrencyLimitNode(), new ValueMergeNode(), new WorkflowFixture.ActionNode(async (context, token) =>
        {
            Assert.Equal(1, limiter.Active);
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return outcome == EOutcome.Succeeded ? NodeResult.Success(context.Inputs["value"])
                                                 : NodeResult.Failure("fixture.child", "fixture", outcome);
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.ConcurrencyLimiter = limiter;
        WorkflowDefinition source = ConcurrencyWorkflowTests.Definition(false);
        WorkflowDefinition definition = source with
        {
            Nodes = [.. source.Nodes, WorkflowFixture.Definition("merge", ValueMergePorts.NodeType)],
            Wires = [new("limit", "value", "merge", "input1"), new("merge", "value", "body", "value")],
        };
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition), cancellationToken: timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(1, limiter.Active);
            Assert.Equal(0, limiter.Released);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        ExecutionReport completed = await running.ConfigureAwait(true);
        Assert.Equal(outcome, completed.Outcome);
        Assert.Equal(outcome, completed.Nodes.Single(node => node.NodeId is "limit").Outcome);
        Assert.Equal(1, limiter.Released);
        Assert.Equal(1, calls);
    }

    /// <summary>区間の取消でも後続を回収してから解放します。</summary>
    /// <returns>WF中断と所有数ゼロです。</returns>
    [Fact]
    public async Task CancelledRunningScopeReturnsPermitAsync()
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = ConcurrencyWorkflowTests.Create(async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return NodeResult.Success();
            }
            catch (OperationCanceledException)
            {
                Assert.Equal(1, limiter.Active);
                return NodeResult.Failure("fixture.cancelled", "Stopped", EOutcome.Cancelled);
            }
        }, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, ConcurrencyWorkflowTests.Definition(true)), cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        await cancellation.CancelAsync().ConfigureAwait(true);
        Assert.Equal(EOutcome.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
        Assert.Equal(0, limiter.Active);
        Assert.Equal(1, limiter.Released);
    }

    /// <summary>制限区間を追加しても、反復を指定したProgressの表示単位を増やしません。</summary>
    /// <returns>反復外のProgressはWFの所有へ残る確認です。</returns>
    [Fact]
    public async Task GateDoesNotCreateAnIterationProgressSessionAsync()
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        NodePreviewStore store = new();
        NodePreviewQuery query = new("limited", ["progress"]);
        WorkflowFixture fixture = new(new ConcurrencyLimitNode(), new ProgressBarNode(store), new WorkflowFixture.ActionNode(async (_, token) =>
        {
            while (store.Read(query).Count == 0)
            {
                await Task.Delay(5, token).ConfigureAwait(false);
            }
            NodePreviewSnapshot snapshot = Assert.Single(store.Read(query));
            Assert.Equal(Guid.Empty, snapshot.Session!.ScopeId);
            return NodeResult.Success();
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.ConcurrencyLimiter = limiter;
        fixture.Engine.CompleteProgress = (session, _, _) =>
        {
            store.CompleteProgress(session);
            return Task.CompletedTask;
        };
        WorkflowDefinition source = ConcurrencyWorkflowTests.Definition(false);
        WorkflowDefinition definition = source with
        {
            Nodes = [.. source.Nodes, WorkflowFixture.Definition("progress", "control.progress", "{\"progressScope\":\"iteration\"}") with
            {
                Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["total"] = NodeValue.FromObject(10) },
            }],
            Wires = [new("limit", "value", "progress", "current"), new("limit", "value", "body", "value")],
        };
        ExecutionReport result = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Empty(store.Read(query));
    }
}
