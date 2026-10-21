using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>明示制御で未開始処理を止め、開始済み処理と要素の結果を混線させないことを検証します。</summary>
public sealed class IterationControlTests
{
    /// <summary>複数PCから届いた開始信号を値の配列と混同せず、反復全体の制御として受けます。</summary>
    /// <returns>収集先Peerの選択に左右されない一回のbreakです。</returns>
    [Fact]
    public async Task SignalsFromMultipleTargetsStillBreakTheLoopAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Create((context, _) =>
        {
            calls++;
            return ValueTask.FromResult(IterationControlTestNode.Result(context.Inputs["value"].Json.GetInt32(), true));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Peers.Others = [new("b", Guid.NewGuid(), "host-b", "127.0.0.2", 0, Contracts.Cluster.EPeerStatus.Ready, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow)];
        fixture.Dispatcher.Handler = (request, token) =>
        {
            string condition = "control.ifStart";
            if (string.Equals(request.Node.Type, condition, StringComparison.Ordinal))
            {
                return Task.FromResult(new NodeResult(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue> { ["start"] = NodeValue.FromObject(true) }));
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        WorkflowDefinition source = IterationResultTests.Definition(false);
        NodeDefinition choose = WorkflowFixture.Definition("choose", "control.ifStart") with { Target = new(EExecutionScope.All, ["a", "b"]) };
        WorkflowDefinition document = source with
        {
            Nodes = [.. source.Nodes.Select(node => node with { Target = new(EExecutionScope.One, ["a"]) }), choose],
            Wires = [.. source.Wires, new("body", "condition", "choose", "condition"), new("choose", "start", "end", "break")],
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, document)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal("[1]", IterationResultTests.Outputs(report)["value"].Json.GetRawText());
    }

    /// <summary>遅い別経路の終了を待ちながら、後続を省略し、未完成の結果の組は全列から除きます。</summary>
    /// <returns>continue/breakの切替と、値が揃った場合・未到着の場合の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitControlWaitsForStartedWorkAndSkipsUnstartedWorkAsync(bool breakLoop, bool missingResult)
    {
        TaskCompletionSource gateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource controlled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<int> late = new();
        WorkflowFixture fixture = Create(async (context, token) =>
        {
            int value = context.Inputs["value"].Json.GetInt32();
            switch (context.NodeId)
            {
                case "gate" when value == 2:
                    gateStarted.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    break;
                case "body" when value == 2:
                    await gateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    break;
                case "late":
                    late.Enqueue(value);
                    break;
            }
            return IterationControlTestNode.Result(value, value == 2);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ObserveControl(fixture, controlled);
        string port = IterationNodeTypes.ContinuePort;
        if (breakLoop)
        {
            port = IterationNodeTypes.BreakPort;
        }
        WorkflowDefinition document = Controlled(port, missingResult);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, document));
        try
        {
            await controlled.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.False(running.IsCompleted);
            Assert.DoesNotContain(2, late);
        }
        finally
        {
            release.TrySetResult();
        }
        ExecutionReport report = await running.ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, System.Text.Json.JsonSerializer.Serialize(report));
        int[] expected = [1, 2, 3];
        int[] expectedLate = [1, 3];
        int started = 3;
        if (breakLoop)
        {
            expected = [1, 2];
            expectedLate = [1];
            started = 2;
        }
        if (missingResult)
        {
            expected = expected.Where(value => value != 2).ToArray();
        }
        IReadOnlyDictionary<string, NodeValue> outputs = IterationResultTests.Outputs(report);
        Assert.Equal(expected, outputs["value"].Json.EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(expected.Length, outputs["value2"].Json.GetArrayLength());
        Assert.Equal(expectedLate, late);
        Assert.Equal(started, IterationTests.Loop(report).Iteration!.Started);
        Assert.Equal(3, IterationTests.Loop(report).Iteration!.Count);
    }

    /// <summary>並列のbreakは追加要素を始めず、開始済みの別要素は確定結果を待ちます。</summary>
    /// <returns>順序を保つ部分配列と、開始数の上限です。</returns>
    [Fact]
    public async Task ParallelBreakAwaitsAlreadyStartedElementsAsync()
    {
        TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource controlled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<int> started = new();
        WorkflowFixture fixture = Create(async (context, token) =>
        {
            int value = context.Inputs["value"].Json.GetInt32();
            started.Enqueue(value);
            if (value == 1)
            {
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
            else
            {
                secondStarted.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
            return IterationControlTestNode.Result(value, value == 1);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ObserveControl(fixture, controlled);
        WorkflowDefinition baseGraph = IterationResultTests.Definition(true, "[1,2,3,4,5]");
        WorkflowDefinition document = baseGraph with
        {
            Nodes = [.. baseGraph.Nodes, WorkflowFixture.Definition("choose", "control.ifStart")],
            Wires = [.. baseGraph.Wires, new("body", "condition", "choose", "condition"), new("choose", "start", "end", "break")],
        };
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, document));
        try
        {
            await controlled.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.False(running.IsCompleted);
            Assert.Equal([1, 2], started.Order());
        }
        finally
        {
            release.TrySetResult();
        }
        ExecutionReport report = await running.ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(2, IterationTests.Loop(report).Iteration!.Started);
        Assert.Equal("[1,2]", IterationResultTests.Outputs(report)["value"].Json.GetRawText());
        Assert.Equal([1, 2], started.Order());
    }

    /// <summary>同じ完了信号が両方へ接続されても、breakを一回だけ採用します。</summary>
    /// <returns>同時の制御信号が追加要素を起動しない確認です。</returns>
    [Fact]
    public async Task BreakWinsWhenBothSignalsArriveTogetherAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = Create((context, _) =>
        {
            calls++;
            return ValueTask.FromResult(IterationControlTestNode.Result(context.Inputs["value"].Json.GetInt32()));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition source = IterationResultTests.Definition(false);
        WorkflowDefinition document = source with { Wires = [.. source.Wires, new("body", CompletionPorts.Signal, "end", "continue"), new("body", CompletionPorts.Signal, "end", "break")] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, document)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal("[1]", IterationResultTests.Outputs(report)["value"].Json.GetRawText());
    }

    /// <summary>制御後に開始済み処理の失敗が判明しても、breakによる正常終了に覆いません。</summary>
    /// <returns>失敗と結果不明を保持した全体結果です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed)]
    [InlineData(EOutcome.Unknown)]
    public async Task BreakDoesNotHideFailureFromAlreadyStartedWorkAsync(EOutcome failure)
    {
        TaskCompletionSource gateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource controlled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = Create(async (context, token) =>
        {
            int value = context.Inputs["value"].Json.GetInt32();
            string gate = "gate";
            if (value == 2 && string.Equals(context.NodeId, gate, StringComparison.Ordinal))
            {
                gateStarted.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                return NodeResult.Failure("test.startedFailure", "Started work did not succeed.", failure);
            }
            if (value == 2)
            {
                await gateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
            return IterationControlTestNode.Result(value, value == 2);
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ObserveControl(fixture, controlled);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, Controlled("break", false)));
        try
        {
            await controlled.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        ExecutionReport report = await running.ConfigureAwait(true);
        Assert.Equal(failure, report.Outcome);
        Assert.Equal(2, IterationTests.Loop(report).Iteration!.Started);
    }

    /// <summary>通常ノード・実条件ゲート・実反復を同じランタイムへ登録します。</summary>
    /// <returns>副作用と信号到着を観測する構成です。</returns>
    internal static WorkflowFixture Create(Func<NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> execute)
        => new(new ForEachNode(), new ForEachNode(true), new ForEachEndNode(), new IfNode(), new IterationControlTestNode(execute));

    /// <summary>制御信号を受けたEndの実配送まで待てるようにします。</summary>
    internal static void ObserveControl(WorkflowFixture fixture, TaskCompletionSource controlled)
    {
        fixture.Dispatcher.Handler = async (request, token) =>
        {
            if (IterationNodeTypes.IsEnd(request.Node.Type) && IterationNodeTypes.RequestedControl(request.Inputs) is not null)
            {
                controlled.TrySetResult();
            }
            return await fixture.Executor.ExecuteAsync(request, token).ConfigureAwait(false);
        };
    }

    /// <summary>制御と並走する待機経路を作り、その先の未開始処理を確認します。</summary>
    /// <returns>同一要素の二つの結果を収集する定義です。</returns>
    private static WorkflowDefinition Controlled(string control, bool missingResult)
    {
        WorkflowDefinition source = IterationResultTests.Definition(false);
        string second = "value2", third = "value3";
        WireDefinition result = new("body", "text", "end", second);
        if (missingResult)
        {
            result = new("gate", "value", "end", second);
        }
        return source with
        {
            Nodes = [.. source.Nodes, WorkflowFixture.Definition("gate", "test.iteration"), WorkflowFixture.Definition("late", "test.iteration"), WorkflowFixture.Definition("choose", "control.ifStart")],
            Wires = [.. source.Wires.Where(wire => !string.Equals(wire.ToPort, second, StringComparison.Ordinal) && !string.Equals(wire.ToPort, third, StringComparison.Ordinal)),
                new("each", "item", "gate", "value"), new("gate", "value", "late", "value"),
                new("body", "condition", "choose", "condition"), new("choose", "start", "end", control), result],
        };
    }
}
