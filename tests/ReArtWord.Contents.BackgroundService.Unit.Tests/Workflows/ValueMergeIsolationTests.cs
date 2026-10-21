using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>遅い後続と新しい到着が重なっても、一件の複数項目を混線させません。</summary>
public sealed class ValueMergeIsolationTests
{
    private static readonly int[] firstArguments = [101, 102];
    private static readonly int[] secondArguments = [201, 202];

    /// <summary>Aの文字列だけ先に読み、Bを完了させてからAの残りを評価します。</summary>
    /// <returns>別Requestと同一Requestの両方で、保持済み値にも汚染されない確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SlowDownstreamKeepsEveryFieldFromItsOwnArrivalAsync(bool sameRequest, bool withIteration)
    {
        TaskCompletionSource startedA = new(TaskCreationOptions.RunContinuationsAsynchronously), releaseA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishedB = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<(string Address, int Number, string Arguments, bool Enabled, Guid Event)> observed = new();
        ConcurrentDictionary<string, Guid> sourceRequests = new(StringComparer.Ordinal);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        string valuePort = "value", addressA = "/a", addressB = "/b";
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            switch (context.NodeId)
            {
                case "a":
                    sourceRequests[context.NodeId] = context.CausationId;
                    return ValueMergeFixture.Result(NodeValue.FromObject(new { address = addressA, number = 11, arguments = firstArguments, enabled = false }));
                case "b":
                    await startedA.Task.WaitAsync(token).ConfigureAwait(false);
                    sourceRequests[context.NodeId] = context.CausationId;
                    return ValueMergeFixture.Result(NodeValue.FromObject(new { address = addressB, number = 22, arguments = secondArguments, enabled = true }));
                case "slow":
                    if (string.Equals(context.Inputs[valuePort].GetString(), addressA, StringComparison.Ordinal))
                    {
                        startedA.TrySetResult();
                        await releaseA.Task.WaitAsync(token).ConfigureAwait(false);
                    }
                    return ValueMergeFixture.Result(context.Inputs["value"]);
                case "sink":
                    string address = context.Inputs["address"].GetString()!;
                    observed.Enqueue((address, context.Inputs["number"].Json.GetInt32(), context.Inputs["arguments"].Json.GetRawText(), context.Inputs["enabled"].Json.GetBoolean(), context.CausationId));
                    if (string.Equals(address, addressB, StringComparison.Ordinal))
                    {
                        finishedB.TrySetResult();
                    }
                    return NodeResult.Success();
                default:
                    throw new InvalidOperationException(context.NodeId);
            }
        });
        WorkflowFixture fixture = new(probe, new ValueMergeNode(), new ForEachNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = new TestStateStore();
        WorkflowDefinition definition = new("isolated-values", "Isolated values", [Node("a"), Node("b"), Node("merge", ValueMergePorts.NodeType), Node("slow"), Node("sink")],
            [new("a", "value", "merge", "input1"), new("b", "value", "merge", "input2"),
                new("merge", "start", "slow", "trigger"), new("merge", "value", "slow", "value", Path: "/address"),
                new("merge", "start", "sink", "trigger"), new("slow", "value", "sink", "address"),
                new("merge", "value", "sink", "number", Path: "/number"), new("merge", "value", "sink", "arguments", Path: "/arguments"), new("merge", "value", "sink", "enabled", Path: "/enabled")]);
        if (withIteration)
        {
            NodeDefinition loop = Node("loop", IterationNodeTypes.ForEach) with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(Array.Empty<int>()) } };
            definition = definition with { Nodes = [.. definition.Nodes, loop] };
        }
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, definition);
        string? entry = null;
        if (!sameRequest)
        {
            entry = "a";
        }
        Task<ExecutionReport> first = fixture.Engine.ExecuteAsync(workflow, entry, cancellationToken: timeout.Token);
        Task<ExecutionReport>? second = null;
        try
        {
            await startedA.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            if (!sameRequest)
            {
                second = fixture.Engine.ExecuteAsync(workflow, "b", cancellationToken: timeout.Token);
            }
            await finishedB.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.False(first.IsCompleted);
            (string Address, int Number, string Arguments, bool Enabled, Guid Event) b = Assert.Single(observed);
            Assert.Equal(("/b", 22, "[201,202]", true), (b.Address, b.Number, b.Arguments, b.Enabled));
            if (second is not null)
            {
                Assert.Equal(EOutcome.Succeeded, (await second.ConfigureAwait(true)).Outcome);
            }
        }
        finally
        {
            releaseA.TrySetResult();
            await first.ConfigureAwait(true);
            if (second is not null)
            {
                await second.ConfigureAwait(true);
            }
        }
        Assert.Equal(EOutcome.Succeeded, (await first.ConfigureAwait(true)).Outcome);
        Assert.Equal(2, observed.Count);
        (string Address, int Number, string Arguments, bool Enabled, Guid Event) a = observed.Last();
        Assert.Equal(("/a", 11, "[101,102]", false), (a.Address, a.Number, a.Arguments, a.Enabled));
        Assert.Equal(2, observed.Select(item => item.Event).Distinct().Count());
        Assert.Equal(sameRequest, sourceRequests["a"] == sourceRequests["b"]);
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>待機中の後続へ取消を伝え、親の完了後に実行を残しません。</summary>
    /// <returns>所有する子実行の終了確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingParentWaitsForItsSlowChildAsync(bool beforeArrival)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int stopped = 0;
        string sink = "sink";
        if (beforeArrival)
        {
            sink = "a";
        }
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            if (string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                started.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return NodeResult.Failure("test.cancelled", "Stopped", EOutcome.Cancelled);
                }
                finally
                {
                    Interlocked.Increment(ref stopped);
                }
            }
            return ValueMergeFixture.Result(NodeValue.FromObject(42));
        });
        WorkflowFixture fixture = new(probe, new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("cancel-values", "Cancel values", [Node("a"), Node("merge", ValueMergePorts.NodeType), Node("sink")],
            [new("a", "value", "merge", "input1"), new("merge", "start", "sink", "trigger"), new("merge", "value", "sink", "value")]);
        fixture.Dispatcher.Handler = (request, _) => fixture.Executor.ExecuteAsync(request, CancellationToken.None);
        fixture.Engine.NodeCancellationHandler = async (request, token) => await fixture.Executor.RequestCancellationAsync(request.Id, request.ExpiresAt, token).ConfigureAwait(false);
        Guid parent = Guid.NewGuid();
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "a", executionId: parent);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.True(await fixture.Engine.RequestCancellationAsync(parent).ConfigureAwait(true));
            Assert.Equal(EOutcome.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            release.TrySetResult();
            await running.ConfigureAwait(true);
        }
        Assert.Equal(1, stopped);
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>試験で共通の空設定を使います。</summary>
    /// <returns>有限ノードの定義です。</returns>
    private static NodeDefinition Node(string id, string type = "test.value") => WorkflowFixture.Definition(id, type);
}
