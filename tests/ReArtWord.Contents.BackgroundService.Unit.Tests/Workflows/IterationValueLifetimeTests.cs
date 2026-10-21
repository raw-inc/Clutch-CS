using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>終了した反復のBinaryを累積保持せず、同じ要素の参照と子処理の寿命は維持します。</summary>
public sealed class IterationValueLifetimeTests
{
    private static readonly int[] elements = [1, 2, 3];

    /// <summary>総出力量が64MiBを超えても、逐次・並列の消費済み要素が後続の保持枠を奪いません。</summary>
    /// <returns>500要素の混線防止・一回性・完了結果を実ランタイムで確認します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumedBinaryOutputsAreReleasedAtTheElementBoundaryAsync(bool parallel)
    {
        ConcurrentDictionary<int, int> generated = [], received = [];
        string producer = "body";
        WorkflowFixture fixture = IterationTests.Create(async (context, token) =>
        {
            if (string.Equals(context.NodeId, producer, StringComparison.Ordinal))
            {
                int value = context.Inputs["value"].Json.GetInt32();
                generated.AddOrUpdate(value, 1, (_, count) => count + 1);
                byte[] bytes = new byte[196608];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                return NodeResult.Success(NodeValue.FromBytes(bytes));
            }
            int observed = BinaryPrimitives.ReadInt32LittleEndian(context.Inputs["value"].Binary.Span);
            received.AddOrUpdate(observed, 1, (_, count) => count + 1);
            return NodeResult.Success(NodeValue.FromObject(observed));
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = IterationTests.Definition(JsonSerializer.Serialize(Enumerable.Range(0, 500)), parallel);
        definition = definition with { Nodes = [.. definition.Nodes, WorkflowFixture.Definition("consume")],
            Wires = [new("each", "item", "body", "value"), new("body", "value", "consume", "value"), new("each", "loop", "end", "loop"), new("consume", "value", "end", "value")] };
        if (parallel)
        {
            string start = "each";
            definition = definition with { Nodes = definition.Nodes.Select(node => string.Equals(node.Id, start, StringComparison.Ordinal) ? node with { Settings = JsonSerializer.SerializeToElement(new { maxConcurrency = 16 }) }
                                                                                                                                           : node).ToArray() };
        }
        ExecutionReport result = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, definition)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(500, generated.Count);
        Assert.Equal(500, received.Count);
        Assert.All(generated.Values.Concat(received.Values), count => Assert.Equal(1, count));
        NodeValue collected = Assert.Single(result.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs["value"];
        Assert.Equal(Enumerable.Range(0, 500), collected.Json.EnumerateArray().Select(value => value.GetInt32()));
    }

    /// <summary>反復の生産が先に終わっても、遅い子処理が各要素の値を取り違えません。</summary>
    /// <returns>後続の終了待ち、元の値の維持、再実行の抑止です。</returns>
    [Fact]
    public async Task SlowChildrenKeepTheirElementValuesAfterTheProducerFinishesAsync()
    {
        TaskCompletionSource produced = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentBag<int> seen = [];
        int generated = 0;
        string body = "body", slow = "slow";
        ValueMergeFixture.Probe probe = new(async (context, token) =>
        {
            if (string.Equals(context.NodeId, body, StringComparison.Ordinal))
            {
                int index = context.Inputs["value"].Json.GetInt32();
                byte[] bytes = new byte[1048576];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, index);
                if (Interlocked.Increment(ref generated) == 3)
                {
                    produced.TrySetResult();
                }
                return NodeResult.Success(NodeValue.FromBytes(bytes));
            }
            int value = BinaryPrimitives.ReadInt32LittleEndian(context.Inputs["value"].Binary.Span);
            if (string.Equals(context.NodeId, slow, StringComparison.Ordinal))
            {
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                return NodeResult.Success(NodeValue.FromObject(value));
            }
            Assert.Equal(value, context.Inputs["number"].Json.GetInt32());
            seen.Add(value);
            return NodeResult.Success();
        });
        WorkflowFixture fixture = new(new ForEachNode(), new ValueMergeNode(), probe);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition loop = WorkflowFixture.Definition("loop", IterationNodeTypes.ForEach) with { Inputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(elements) } };
        WorkflowDefinition definition = new("children", "Element ownership", [loop, WorkflowFixture.Definition(body, "test.value"), WorkflowFixture.Definition("merge", ValueMergePorts.NodeType),
            WorkflowFixture.Definition(slow, "test.value"), WorkflowFixture.Definition("sink", "test.value")],
            [new("loop", "item", body, "value"), new(body, "value", "merge", "input1"), new("merge", "start", slow, "trigger"), new("merge", "value", slow, "value"),
             new(slow, "value", "sink", "number"), new("merge", "value", "sink", "value")]);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition));
        try
        {
            await produced.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(EOutcome.Succeeded, (await running.ConfigureAwait(true)).Outcome);
        Assert.Equal(3, generated);
        Assert.Equal(elements, seen.Order());
    }
}
