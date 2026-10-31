using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>同一キャンバスの反復が実ノードの完了を待ち、入力を分離することを確認します。</summary>
public sealed class IterationTests
{
    /// <summary>Endの有無に関係なく各要素の実完了後に次を開始します。</summary>
    /// <returns>逐次順序と集約の確認です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SequentialWaitsForEachBodyAndAllowsOmittedEndAsync(bool end)
    {
        List<int> order = [];
        List<Guid> ids = [];
        HashSet<Guid> causes = [];
        int active = 0;
        WorkflowFixture fixture = Create(async (context, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            int value = context.Inputs["value"].Json.GetInt32();
            order.Add(value);
            ids.Add(context.ExecutionId);
            causes.Add(context.CausationId);
            await Task.Delay(10, token).ConfigureAwait(false);
            order.Add(-value);
            Interlocked.Decrement(ref active);
            return NodeResult.Success(NodeValue.FromObject(value * 2));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, Definition(end: end))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal([1, -1, 2, -2, 3, -3], order);
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(report.Id, Assert.Single(causes));
        IterationExecutionSummary summary = Loop(report).Iteration!;
        Assert.Equal(3, summary.Count);
        Assert.Equal(3, summary.Started);
        Assert.Equal([0, 1, 2], summary.Items.Select(item => item.Index));
        if (end)
        {
            Assert.Equal("[2,4,6]", Collected(report).Json.GetRawText());
        }
    }

    /// <summary>固定数の要素だけを並走させ、完成順に関係なく元の配列順で返します。</summary>
    /// <returns>同時実行数と結果の順序です。</returns>
    [Fact]
    public async Task ParallelBodiesAreBoundedAndCollectedInInputOrderAsync()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        ConcurrentBag<int> concurrency = [];
        WorkflowFixture fixture = Create(async (context, token) =>
        {
            int current = Interlocked.Increment(ref active);
            concurrency.Add(current);
            if (current == 2)
            {
                entered.TrySetResult();
            }
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            int value = context.Inputs["value"].Json.GetInt32();
            await Task.Delay((4 - value) * 10, token).ConfigureAwait(false);
            Interlocked.Decrement(ref active);
            return NodeResult.Success(NodeValue.FromObject(value));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(Compile(fixture, Definition(parallel: true)), cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.False(running.IsCompleted);
            release.TrySetResult();
            ExecutionReport report = await running.ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            Assert.Equal(2, concurrency.Max());
            Assert.Equal("[1,2,3]", Collected(report).Json.GetRawText());
        }
        finally
        {
            release.TrySetResult();
            await cancellation.CancelAsync().ConfigureAwait(true);
            await running.ConfigureAwait(true);
        }
    }

    /// <summary>空配列は正常なゼロ回実行です。</summary>
    /// <returns>ボディを呼ばない確認です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyArrayDoesNotExecuteBodyAsync(bool end)
    {
        int calls = 0;
        WorkflowFixture fixture = Create((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, Definition("[]", end: end))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(0, calls);
        Assert.Equal(0, Loop(report).Iteration!.Started);
        if (end)
        {
            Assert.Equal("[]", Collected(report).Json.GetRawText());
        }
    }

    /// <summary>未入力・非配列を空配列として正常化しません。</summary>
    /// <returns>副作用未開始です。</returns>
    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    public async Task InvalidArrayNeverStartsBodyAsync(string input)
    {
        int calls = 0;
        WorkflowFixture fixture = Create((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(input);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        if (compiled.Workflow is not null)
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow).ConfigureAwait(true);
            Assert.Equal(EOutcome.NotExecuted, report.Outcome);
        }
        Assert.Equal(0, calls);
    }

    /// <summary>後続は集約された配列を一回だけ受け取ります。</summary>
    /// <returns>ボディと後処理の呼出回数です。</returns>
    [Fact]
    public async Task EndRunsContinuationOnceAndPreservesNullAndObjectsAsync()
    {
        int after = 0;
        WorkflowFixture fixture = Create((context, _) =>
        {
            string continuation = "after";
            if (string.Equals(context.NodeId, continuation, StringComparison.Ordinal))
            {
                after++;
                Assert.Equal("[null,{\"age\":2},[3]]", context.Inputs["value"].Json.GetRawText());
            }
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"]));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition("[null,{\"age\":2},[3]]");
        definition = definition with { Nodes = [.. definition.Nodes, WorkflowFixture.Definition("after")], Wires = [.. definition.Wires, new("end", "value", "after", "value")] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(1, after);
    }

    /// <summary>入れ子の各要素は独立したフレームで結果を集約します。</summary>
    /// <returns>二重配列を維持した結果です。</returns>
    [Fact]
    public async Task NestedLoopsCollectEachInnerArraySeparatelyAsync()
    {
        ConcurrentBag<int> observed = [];
        WorkflowFixture fixture = Create((context, _) =>
        {
            observed.Add(context.Inputs["value"].Json.GetInt32());
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"]));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition outer = Definition("[[1,2],[3,4]]").Nodes[0];
        WorkflowDefinition definition = new("nested", "Nested", [outer, WorkflowFixture.Definition("inner", IterationNodeTypes.ParallelForEach, "{\"maxConcurrency\":2}"), WorkflowFixture.Definition("body"), WorkflowFixture.Definition("inner-end", IterationNodeTypes.End), WorkflowFixture.Definition("end", IterationNodeTypes.End)],
            [new("each", "item", "inner", "value"), new("each", "loop", "end", "loop"), new("inner", "item", "body", "value"), new("inner", "loop", "inner-end", "loop"), new("body", "value", "inner-end", "value"), new("inner-end", "value", "end", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal([1, 2, 3, 4], observed.Order());
        Assert.Equal("[[1,2],[3,4]]", Collected(report).Json.GetRawText());
    }

    /// <summary>実際の通常ノード実行境界を備えた試験用の構成を作ります。</summary>
    /// <returns>逐次・並列・終了を登録した実行環境です。</returns>
    internal static WorkflowFixture Create(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> handler) => new(new ForEachNode(), new ForEachNode(true), new ForEachEndNode(), new WorkflowFixture.ActionNode(handler, true));

    /// <summary>同じキャンバスの最小反復定義を作ります。</summary>
    /// <returns>入口、処理、任意の終了です。</returns>
    internal static WorkflowDefinition Definition(string json = "[1,2,3]", bool parallel = false, bool end = true)
    {
        string type = IterationNodeTypes.ForEach;
        string settings = "{}";
        if (parallel)
        {
            type = IterationNodeTypes.ParallelForEach;
            settings = "{\"maxConcurrency\":2}";
        }
        NodeDefinition start = WorkflowFixture.Definition("each", type, settings) with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromJson(NodeSettings.Schema(json)) } };
        List<NodeDefinition> nodes = [start, WorkflowFixture.Definition("body")];
        List<WireDefinition> wires = [new("each", "item", "body", "value")];
        if (end)
        {
            nodes.Add(WorkflowFixture.Definition("end", IterationNodeTypes.End));
            wires.Add(new("each", "loop", "end", "loop"));
            wires.Add(new("body", "value", "end", "value"));
        }
        return new("iteration", "Iteration", nodes, wires);
    }

    /// <summary>コンパイル失敗を具体的な試験失敗として表示します。</summary>
    /// <returns>実行可能な固定版です。</returns>
    internal static CompiledWorkflow Compile(WorkflowFixture fixture, WorkflowDefinition definition)
    {
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.True(compiled.Workflow is not null, string.Join("; ", compiled.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        return compiled.Workflow;
    }

    /// <summary>開始ノードの結果を取り出します。</summary>
    /// <returns>反復概要です。</returns>
    internal static NodeExecutionReport Loop(ExecutionReport report) => Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "each", StringComparison.Ordinal));

    /// <summary>成功した終了の配列を取得します。</summary>
    /// <returns>入力順の値です。</returns>
    private static NodeValue Collected(ExecutionReport report) => Assert.Single(Assert.Single(report.Nodes, node => string.Equals(node.NodeId, "end", StringComparison.Ordinal)).Targets).Result.Outputs["value"];
}
