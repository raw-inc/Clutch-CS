using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>合流で分離した要素と元要求の値、明示した要求間保持の境界を確認します。</summary>
public sealed class IterationEventContextTests
{
    /// <summary>同時に二要求を反復し、一方の後続だけが遅れても要求値と要素を混ぜません。</summary>
    /// <returns>逐次・並列のどちらでも各要素が元の要求値を保持する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachMergedElementKeepsItsOriginalRequestAsync(bool parallel)
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<(int Request, int Element)> observed = new();
        int sources = 0;
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            string source = "source";
            if (string.Equals(context.NodeId, source, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref sources);
                int request = context.Inputs["value"].Json.GetInt32();
                return ValueMergeFixture.Result(NodeValue.FromObject(new { request, items = new[] { request * 10, request * 10 + 1, request * 10 + 2 } }));
            }
            int number = context.Inputs["number"].Json.GetInt32();
            if (number == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            observed.Enqueue((number, context.Inputs["value"].Json.GetInt32()));
            return NodeResult.Success();
        });
        ForEachNode loop = new(parallel);
        WorkflowFixture fixture = new(probe, loop, new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("element-context", "Element context", [Node("source"), Node("loop", loop.Descriptor.Type), Node("merge", ValueMergePorts.NodeType), Node("sink")],
            [new("source", "value", "loop", "value", Path: "/items"), new("loop", "item", "merge", "input1"),
                new("merge", "start", "sink", "trigger"), new("merge", "value", "sink", "value"), new("source", "value", "sink", "number", Path: "/request")]);
        Runtime.Workflows.Compilation.CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<ExecutionReport> first = fixture.Engine.ExecuteAsync(workflow, "source", Input(1), cancellationToken: timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            ExecutionReport second = await fixture.Engine.ExecuteAsync(workflow, "source", Input(2), cancellationToken: timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, second.Outcome);
            Assert.Equal(3, observed.Count);
            Assert.All(observed, item => Assert.Equal(2, item.Request));
        }
        finally
        {
            release.TrySetResult();
            await first.ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, (await first.ConfigureAwait(true)).Outcome);
        Assert.Equal(2, sources);
        Assert.Equal(6, observed.Count);
        Assert.All(observed, item => Assert.Equal(item.Request, item.Element / 10));
        Assert.Equal(6, observed.Distinct().Count());
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>全入力合流で別要求の完了を待つ既存用途では、明示した信号付きの保持値を読めます。</summary>
    /// <returns>現在の要求値の優先と、明示的な要求間保持を両立する確認です。</returns>
    [Fact]
    public async Task AcrossRequestAllStillReadsExplicitlyRetainedValuesAsync()
    {
        ConcurrentQueue<(int First, int Second)> observed = new();
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            string sink = "sink";
            if (string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                observed.Enqueue((context.Inputs["value"].Json.GetInt32(), context.Inputs["number"].Json.GetInt32()));
                return ValueTask.FromResult(NodeResult.Success());
            }
            return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
        });
        WorkflowFixture fixture = new(probe, new SignalMergeNode("all", new TestStateStore()));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = new TestStateStore();
        WorkflowDefinition definition = new("retained-all", "Retained all", [Node("a"), Node("b"), Node("readA"), Node("readB"), Node("merge", "control.signal.all"), Node("sink")],
            [new("a", "value", "readA", "value"), new("a", CompletionPorts.Signal, "readA", "trigger"),
                new("b", "value", "readB", "value"), new("b", CompletionPorts.Signal, "readB", "trigger"),
                new("readA", CompletionPorts.Signal, "merge", "input1"), new("readB", CompletionPorts.Signal, "merge", "input2"),
                new("merge", "start", "sink", "trigger"), new("readA", "value", "sink", "value"), new("readB", "value", "sink", "number")]);
        Runtime.Workflows.Compilation.CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Engine.ExecuteAsync(workflow, "a", Input(10)).ConfigureAwait(true)).Outcome);
        Assert.Empty(observed);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Engine.ExecuteAsync(workflow, "b", Input(20)).ConfigureAwait(true)).Outcome);
        Assert.Equal((10, 20), Assert.Single(observed));
    }

    /// <summary>元要求を識別する値を用意します。</summary>
    /// <returns>実行一回分の入力です。</returns>
    private static Dictionary<string, NodeValue> Input(int value) => new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) };

    /// <summary>必要な種類だけを空設定で配置します。</summary>
    /// <returns>試験するノードです。</returns>
    private static NodeDefinition Node(string id, string type = "test.value") => WorkflowFixture.Definition(id, type);
}
