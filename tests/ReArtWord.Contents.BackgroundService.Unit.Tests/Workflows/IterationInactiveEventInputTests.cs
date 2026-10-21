using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>未選択枝の別入力待ちを、実行した反復要素の失敗へ混ぜないことを検証します。</summary>
public sealed class IterationInactiveEventInputTests
{
    /// <summary>選択されなかった枝に未到着の合流値があっても、採用した値で全要素を完了します。</summary>
    /// <returns>逐次・並列の完了件数と、未選択側を実行しない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnselectedBranchDoesNotWaitForAnUnrelatedEventAsync(bool parallel)
    {
        ConcurrentBag<int> received = [];
        ConcurrentBag<string> branches = [];
        string sink = "sink";
        WorkflowFixture fixture = IterationValueMergeTests.Create((context, _) =>
        {
            if (string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                int index = context.Inputs["number"].Json.GetInt32();
                received.Add(index);
                return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(index)));
            }
            branches.Add(context.NodeId);
            return ValueTask.FromResult(NodeResult.Success(context.Inputs["value"].Copy()));
        }, parallel);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(parallel);
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal)
        {
            ["value"] = NodeValue.FromObject(new { request = "inactive", items = Enumerable.Range(0, 3).Select(index => new { index, selected = false }).ToArray() }),
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "button", inputs, cancellationToken: timeout.Token).ConfigureAwait(true);
        Assert.True(report.Outcome == EOutcome.Succeeded, System.Text.Json.JsonSerializer.Serialize(report));
        Assert.Equal(Enumerable.Range(0, 3), received.Order());
        Assert.Equal(3, branches.Count);
        Assert.All(branches, id => Assert.Equal("right", id));
        Assert.Equal(3, report.Nodes.Single(node => node.NodeId is "loop").Iteration!.Started);
        Assert.Equal("[0,1,2]", Assert.Single(report.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs["value"].Json.GetRawText());
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>開始信号が届いた枝の入力不足は、未選択として無視しません。</summary>
    /// <returns>副作用を開始せず、反復を成功扱いしない確認です。</returns>
    [Fact]
    public async Task SelectedBranchStillRequiresItsConnectedValueAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = IterationValueMergeTests.Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success());
        }, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, Definition(false)), "button", IterationValueMergeTests.Input("selected")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Equal(0, calls);
        Assert.Contains(report.Nodes.Single(node => node.NodeId is "left").Targets, target => target.Result.ReasonCode is "input.unavailable");
        Assert.Equal(1, report.Nodes.Single(node => node.NodeId is "loop").Iteration!.Started);
    }

    /// <summary>非選択が確定していない事象待ちは、反復から無条件に除外しません。</summary>
    /// <returns>開始事象待ちと、開始口を持たないデータ待ちを維持する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingEventWithoutAClosedStartGateStillBlocksTheIterationAsync(bool trigger)
    {
        int calls = 0;
        WorkflowFixture fixture = IterationValueMergeTests.Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(NodeResult.Success());
        }, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition original = Definition(false);
        List<WireDefinition> wires = original.Wires.Where(wire => !(wire.ToNode is "left" && wire.ToPort is "trigger")).ToList();
        if (trigger)
        {
            wires.Add(new("otherEvent", "start", "left", "trigger"));
        }
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, original with { Wires = wires }), "button", IterationValueMergeTests.Input("pending")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Equal(0, calls);
        Assert.Contains(report.Nodes.Single(node => node.NodeId is "left").Targets, target => target.Result.ReasonCode is "input.eventPending");
        Assert.Equal(1, report.Nodes.Single(node => node.NodeId is "loop").Iteration!.Started);
    }

    /// <summary>一方の枝だけが、今回到着しない別イベントのデータにも依存する構成です。</summary>
    /// <returns>最初の採用値は成功しても反復が止まっていた最小構成です。</returns>
    private static WorkflowDefinition Definition(bool parallel)
    {
        WorkflowDefinition original = IterationValueMergeTests.Definition(parallel, true);
        return original with
        {
            Nodes = [.. original.Nodes, WorkflowFixture.Definition("otherButton", "control.button"), WorkflowFixture.Definition("otherEvent", ValueMergePorts.NodeType)],
            Wires = [.. original.Wires, new("otherButton", "value", "otherEvent", "input1"), new("otherEvent", "value", "left", "arguments")],
        };
    }
}
