using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>先行データと子実行の後着イベントを合流し、未到着を確定失敗にしません。</summary>
public sealed class EventPendingOutcomeTests
{
    private static readonly int[] elements = [1, 2];

    /// <summary>先行評価後に反復結果が届けば成功し、実際のボディ失敗は隠しません。</summary>
    /// <returns>一度だけの通知、同じ要求の値、正しい全体成否です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateIterationResultDoesNotLeaveAFalseFailureAsync(bool fail)
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<int> received = new();
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            switch (context.NodeId)
            {
                case "source": return ValueMergeFixture.Result(context.Inputs["value"]);
                case "body":
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token).ConfigureAwait(false);
                    return fail ? NodeResult.Failure("fixture.bodyFailed", "Actual failure")
                                : ValueMergeFixture.Result(NodeValue.FromObject(true));
                default:
                    Assert.Equal(2, context.Inputs["value"].Json.GetArrayLength());
                    received.Enqueue(context.Inputs["number"].Json.GetInt32());
                    return NodeResult.Success();
            }
        });
        WorkflowFixture fixture = new(probe, new SignalMergeNode("each", new TestStateStore()), new ForEachNode(), new ForEachEndNode(), new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition loop = Node("loop", "control.forEach") with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(elements) } };
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, new("pending-event", "Pending event", [Node("source"), Node("gate", "control.signal.each"), loop,
            Node("body"), Node("end", "control.forEach.end"), Node("merge", "control.valueMerge"), Node("sink")],
            [new("source", CompletionPorts.Signal, "gate", "input1"), new("gate", "start", "loop", "trigger"),
                new("loop", "item", "body", "value"), new("loop", "loop", "end", "loop"), new("body", "value", "end", "value"),
                new("end", "value", "merge", "input1"), new("merge", "value", "sink", "value"), new("source", "value", "sink", "number")]));
        WorkflowDebugArm arm = new(Guid.NewGuid(), workflow.Definition.Id, fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        fixture.Engine.Debug.Claim = (_, _, _) => ValueTask.FromResult<bool?>(true);
        Assert.True(fixture.Engine.Debug.Prepare(arm));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(workflow, "source", new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(42) }, cancellationToken: deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(true);
            const string sinkName = "sink";
            while (fixture.Engine.Debug.Read(new(arm.Id, Limit: 50))?.Nodes?.Any(node => string.Equals(node.NodeId, sinkName, StringComparison.Ordinal)) != true)
            {
                await Task.Delay(10, deadline.Token).ConfigureAwait(true);
            }
            Assert.Empty(received);
        }
        finally
        {
            release.TrySetResult();
            await running.ConfigureAwait(true);
        }
        ExecutionReport report = await running.ConfigureAwait(true);
        Assert.Equal(fail ? EOutcome.Failed
                          : EOutcome.Succeeded, report.Outcome);
        if (fail)
        {
            Assert.Empty(received);
        }
        else
        {
            Assert.Equal(42, Assert.Single(received));
        }
    }

    /// <summary>通常の入力不足はイベント待ちへ読み替えません。</summary>
    /// <returns>未供給を隠さず失敗として残す確認です。</returns>
    [Fact]
    public async Task MissingOrdinaryOutputRemainsAFailureAsync()
    {
        const string sourceName = "source";
        WorkflowFixture fixture = new(new ValueMergeFixture.Probe((context, _) => ValueTask.FromResult(context.NodeId is sourceName ? ValueMergeFixture.Result(NodeValue.FromObject(42))
                                                                                                                                    : NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, new("missing-output", "Missing", [Node("source"), Node("empty"), Node("sink")],
            [new("source", "value", "sink", "number"), new("empty", "value", "sink", "value")]));
        ExecutionReport result = await fixture.Engine.ExecuteAsync(workflow, "source").ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Contains(result.Nodes.Single(node => node.NodeId is "sink").Targets, target => target.Result.ReasonCode is "input.unavailable");
    }

    /// <summary>実配送に使用する試験ノードの定義です。</summary>
    /// <returns>空設定のノードです。</returns>
    private static NodeDefinition Node(string id, string type = "test.value") => WorkflowFixture.Definition(id, type);
}
