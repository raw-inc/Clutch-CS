using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>先着後の処理寿命と、欠落・失敗・型保持の境界を検証します。</summary>
public sealed class IterationValueMergeBoundaryTests
{
    /// <summary>別要求範囲や別反復からの値を、現在の要素へ暗黙に取り込みません。</summary>
    /// <returns>同じ要素に閉じない合流は、保存前に拒否する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutsideArrivalsCannotBePulledIntoTheElementAsync(bool otherLoop)
    {
        WorkflowFixture fixture = IterationValueMergeTests.Create((_, _) => ValueTask.FromResult(NodeResult.Success()), false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = IterationValueMergeTests.Definition(false, true);
        List<NodeDefinition> nodes = [.. definition.Nodes];
        List<WireDefinition> wires = definition.Wires.Where(wire => !(wire.ToNode is "merge" && wire.ToPort is "input2")).ToList();
        if (otherLoop)
        {
            nodes.Add(WorkflowFixture.Definition("other", IterationNodeTypes.ForEach) with { Inputs = new Dictionary<string, NodeValue>
            {
                ["value"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>("[{}]")),
            } });
            wires.Add(new("other", "item", "merge", "input2"));
        }
        else
        {
            wires.Add(new("button", "value", "merge", "input2"));
        }
        Runtime.Workflows.Compilation.CompilationResult result = fixture.Compiler.Compile(definition with { Nodes = nodes, Wires = wires });
        Assert.Null(result.Workflow);
        Assert.Contains(result.Issues, issue => issue.Code is "iteration.dependencyCycle");
    }

    /// <summary>遅い枝を待たずに後続を開始し、その枝の終了も回収してから反復を完了します。</summary>
    /// <returns>先着値の固定、後着による二重起動の抑制、終了待ちです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateArrivalIsAwaitedWithoutStartingAnotherBodyAsync(bool parallel)
    {
        ConcurrentDictionary<int, TaskCompletionSource> releases = new();
        ConcurrentBag<int> received = [], late = [];
        string left = "left", right = "right", sink = "sink";
        WorkflowFixture fixture = IterationValueMergeTests.Create(async (context, token) =>
        {
            int index = context.Inputs["value"].Json.GetProperty("index").GetInt32();
            TaskCompletionSource release = releases.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (string.Equals(context.NodeId, right, StringComparison.Ordinal))
            {
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                late.Add(index);
                return NodeResult.Success(NodeValue.FromObject(new { index, side = right }));
            }
            if (string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                Assert.Equal(left, context.Inputs["value"].Json.GetProperty("side").GetString());
                Assert.Equal(index, context.Inputs["number"].Json.GetInt32());
                received.Add(index);
                release.TrySetResult();
                return NodeResult.Success(NodeValue.FromObject(index));
            }
            return NodeResult.Success(NodeValue.FromObject(new { index, side = left }));
        }, parallel);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = IterationValueMergeTests.Definition(parallel, true);
        definition = definition with { Wires = definition.Wires.Where(wire => !(wire.ToNode is "left" or "right" && wire.ToPort is "trigger")).ToArray() };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "button", IterationValueMergeTests.Input("request"), cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(Enumerable.Range(0, 4), received.Order());
        Assert.Equal(Enumerable.Range(0, 4), late.Order());
        Assert.Equal("[0,1,2,3]", Assert.Single(report.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs["value"].Json.GetRawText());
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>null・false・空バイナリを未到着と混同せず、明示した型情報を保持します。</summary>
    /// <returns>同じ要素の値を変換せずに受信した確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrivalsPreserveNullBinaryAndTypeMetadataAsync(bool parallel)
    {
        NodeValue[] values = [NodeValue.Null, NodeValue.FromObject(false), NodeValue.FromBytes(new byte[] { 0, 255 }), NodeValue.FromBytes(ReadOnlyMemory<byte>.Empty),
            PathTypeMetadata.Apply(NodeValue.FromObject("C:/value.txt"), new("path")), PathTypeMetadata.Apply(NodeValue.FromObject(7), new("int64")),
            NodeValue.FromObject(new Dictionary<string, object?> { ["key"] = null })];
        ConcurrentBag<int> received = [];
        string sink = "sink";
        WorkflowFixture fixture = IterationValueMergeTests.Create((context, _) =>
        {
            if (!string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(NodeResult.Success(values[context.Inputs["value"].Json.GetProperty("index").GetInt32()].Copy()));
            }
            int index = context.Inputs["number"].Json.GetInt32();
            NodeValue actual = context.Inputs["value"];
            Assert.Equal(values[index].Kind, actual.Kind);
            Assert.Equal(values[index].Binary.ToArray(), actual.Binary.ToArray());
            Assert.Equal(values[index].Json.GetRawText(), actual.Json.GetRawText());
            if (values[index].DataType is { } expectedType)
            {
                Assert.True(ValueTypeInference.Same(expectedType, actual.DataType));
            }
            received.Add(index);
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(index)));
        }, parallel);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        Dictionary<string, NodeValue> input = new(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject(new { request = "types", items = Enumerable.Range(0, values.Length).Select(index => new { index, selected = index % 2 == 0 }).ToArray() }),
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, IterationValueMergeTests.Definition(parallel, true)), "button", input).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(Enumerable.Range(0, values.Length), received.Order());
    }

    /// <summary>選択された枝の失敗や不明を、未選択側の値やnullへ置換しません。</summary>
    /// <returns>後続を実行せず、元の失敗境界を保持する確認です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, EOutcome.Failed)]
    [InlineData(EOutcome.Unknown, EOutcome.Unknown)]
    [InlineData(EOutcome.Cancelled, EOutcome.Failed)]
    public async Task FailedArrivalDoesNotInventAValueAsync(EOutcome outcome, EOutcome expected)
    {
        int bodies = 0;
        string sink = "sink";
        WorkflowFixture fixture = IterationValueMergeTests.Create((context, _) =>
        {
            if (string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                bodies++;
            }
            return ValueTask.FromResult(NodeResult.Failure("test.failed", "The selected input failed.", outcome));
        }, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, IterationValueMergeTests.Definition(false, true)), "button", IterationValueMergeTests.Input("failure")).ConfigureAwait(true);
        Assert.Equal(expected, report.Outcome);
        Assert.Equal(outcome, report.Nodes.Single(node => node.NodeId is "left").Outcome);
        Assert.Equal(0, bodies);
        Assert.Equal(1, report.Nodes.Single(node => node.NodeId is "loop").Iteration!.Started);
    }

    /// <summary>到着を待っている間の要求取消を、開始済みの枝へ伝えて終了を待ちます。</summary>
    /// <returns>値を補わず、後続も次の要素も開始しない確認です。</returns>
    [Fact]
    public async Task CancellationWhileWaitingDoesNotStartTheBodyAsync()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0, stopped = 0, bodies = 0;
        string sink = "sink";
        WorkflowFixture fixture = IterationValueMergeTests.Create(async (context, token) =>
        {
            if (string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                bodies++;
                return NodeResult.Success();
            }
            Interlocked.Increment(ref started);
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return NodeResult.Success(context.Inputs["value"]);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return NodeResult.Failure("test.cancelled", "The input stopped before producing a value.", EOutcome.Cancelled);
            }
            finally
            {
                Interlocked.Increment(ref stopped);
            }
        }, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task<ExecutionReport> run = fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, IterationValueMergeTests.Definition(false, true)), "button", IterationValueMergeTests.Input("cancel"), cancellationToken: cancellation.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable executionLifetime = new TestTaskLifetime(run, cancellation).ConfigureAwait(true);
        await entered.Task.WaitAsync(cancellation.Token).ConfigureAwait(true);
        await cancellation.CancelAsync().ConfigureAwait(true);
        ExecutionReport report = await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Cancelled, JsonSerializer.Serialize(report));
        Assert.Equal(1, started);
        Assert.Equal(1, stopped);
        Assert.Equal(0, bodies);
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>合流の開始信号によるcontinueとbreakは、同じ要素の未開始処理を止めます。</summary>
    /// <returns>反復の制御が合流を待つ後続より先に観測される確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MergedSignalControlsItsElementBeforeStartingTheBodyAsync(bool parallel, bool breakLoop)
    {
        int bodies = 0;
        string sink = "sink";
        WorkflowFixture fixture = IterationValueMergeTests.Create((context, _) =>
        {
            if (string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref bodies);
            }
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"].Copy()));
        }, parallel);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition original = IterationValueMergeTests.Definition(parallel, true);
        string port = IterationNodeTypes.ContinuePort;
        int started = 4;
        if (breakLoop)
        {
            port = IterationNodeTypes.BreakPort;
            started = 1;
        }
        WorkflowDefinition definition = original with { Wires = [.. original.Wires, new("merge", "start", "end", port)] };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "button", IterationValueMergeTests.Input("control"), cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(0, bodies);
        Assert.Equal(started, report.Nodes.Single(node => node.NodeId is "loop").Iteration!.Started);
        Assert.Equal(0, Assert.Single(report.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs["value"].Json.GetArrayLength());
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>内側の反復と連続した合流でも、各要素の後続を一度だけ実行します。</summary>
    /// <returns>外側・内側の添字と入力値が一致したまま収集される確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NestedLoopsAndChainedMergesKeepTheirScopeAsync(bool parallel, bool chain)
    {
        ConcurrentBag<(int Outer, int Inner, Guid Execution)> observed = [];
        string sink = "sink", merge = "merge";
        WorkflowFixture fixture = IterationValueMergeTests.Create((context, _) =>
        {
            NodeValue value = context.Inputs["value"];
            if (!string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(NodeResult.Success(value.Copy()));
            }
            int outer = context.Inputs["arguments"].Json.GetInt32(), inner = context.Inputs["number"].Json.GetInt32();
            Assert.Equal(outer * 2 + inner, value.Json.GetProperty("index").GetInt32());
            observed.Add((outer, inner, context.ExecutionId));
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(outer * 2 + inner)));
        }, parallel);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition original = IterationValueMergeTests.Definition(parallel, true);
        string type = original.Nodes.Single(node => node.Id is "loop").Type;
        List<NodeDefinition> nodes = [.. original.Nodes, WorkflowFixture.Definition("outer", type), WorkflowFixture.Definition("outerEnd", IterationNodeTypes.End)];
        List<WireDefinition> wires = original.Wires.Where(wire => !(wire.FromNode is "button" && wire.ToNode is "loop")).ToList();
        wires.AddRange([new("button", "start", "outer", "trigger"), new("button", "value/groups", "outer", "value"), new("outer", "item", "loop", "value"),
            new("outer", "index", "sink", "arguments"), new("outer", "loop", "outerEnd", "loop"), new("end", "value", "outerEnd", "value")]);
        if (chain)
        {
            nodes.Add(WorkflowFixture.Definition("merge2", ValueMergePorts.NodeType));
            wires = wires.Select(wire =>
            {
                if (string.Equals(wire.FromNode, merge, StringComparison.Ordinal))
                {
                    return wire with { FromNode = "merge2" };
                }
                return wire;
            }).ToList();
            wires.Add(new("merge", "value", "merge2", "input1"));
        }
        Dictionary<string, NodeValue> input = new(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject(new { request = "nested", groups = Enumerable.Range(0, 2).Select(group => Enumerable.Range(0, 2).Select(index => new { index = group * 2 + index, selected = index == 0 }).ToArray()).ToArray() }),
        };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, original with { Nodes = nodes, Wires = wires }), "button", input).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(report));
        Assert.Equal(4, observed.Count);
        Assert.Equal(4, observed.Select(item => item.Execution).Distinct().Count());
        Assert.Equal("[[0,1],[2,3]]", Assert.Single(report.Nodes.Single(node => node.NodeId is "outerEnd").Targets).Result.Outputs["value"].Json.GetRawText());
    }
}
