using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>値合流を通常の実行・反復・複数PCから駆動します。</summary>
public sealed class ValueMergeWorkflowTests
{
    private static readonly int[] elements = [1, 2, 3];
    private static readonly string[] peerIds = ["a", "b"];

    /// <summary>false・nullも一件とし、同じ値の別到着を取り除きません。</summary>
    /// <returns>JSON・バイナリ・型情報を保った二回ずつの後続です。</returns>
    [Fact]
    public async Task PreservesEveryPayloadAndRepeatedEqualArrivalsAsync()
    {
        List<NodeValue> received = [];
        List<Guid> events = [];
        string sink = "sink";
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            if (string.Equals(context.NodeId, sink, StringComparison.Ordinal))
            {
                received.Add(context.Inputs["value"]);
                events.Add(context.CausationId);
            }
            return ValueTask.FromResult(ValueMergeFixture.Result(context.Inputs["value"]));
        });
        WorkflowFixture fixture = new(probe, new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompiledWorkflow workflow = ValueMergeFixture.Compile(fixture, Definition(Node("source")));
        NodeValue[] payloads = [NodeValue.Null, NodeValue.FromObject(false), NodeValue.FromObject(12), NodeValue.FromObject("text"),
            NodeValue.FromObject(elements), NodeValue.FromObject(new { name = "packet", number = 3 }), NodeValue.FromBytes(new byte[] { 0, 128, 255 }),
            NodeValue.FromObject("C:/data") with { DataType = new("path") }];
        foreach (NodeValue payload in payloads)
        {
            for (int index = 0; index < 2; index++)
            {
                ExecutionReport report = await fixture.Engine.ExecuteAsync(workflow, "source", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = payload }).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, report.Outcome);
                Assert.Equal(ContractJson.Hash(payload), ContractJson.Hash(received[^1]));
            }
        }
        Assert.Equal(payloads.Length * 2, received.Count);
        Assert.Equal(received.Count, events.Distinct().Count());
        _ = await fixture.Engine.ExecuteAsync(workflow, "sink").ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Engine.ExecuteAsync(workflow, "merge").ConfigureAwait(true)).Nodes.Single(node => node.NodeId is "merge").Outcome);
        Assert.Equal(payloads.Length * 2, received.Count);
    }

    /// <summary>一つのノードを複数PCで実行しても、各PCの値を一件ずつ扱います。</summary>
    /// <returns>peerId付き配列へ変換されない二つの後続です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FansInEachTargetWithoutWrappingTheirValuesAsync(bool firstFails)
    {
        ConcurrentQueue<string> received = new();
        string source = "source";
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            received.Enqueue(context.Inputs["value"].GetString()!);
            return ValueTask.FromResult(NodeResult.Success());
        });
        WorkflowFixture fixture = new(probe, new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Peers.Others = [new PeerInfo("b", Guid.NewGuid(), "host-b", "127.0.0.2", 0, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow)];
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, source, StringComparison.Ordinal))
            {
                if (firstFails && string.Equals(request.TargetPeerId, peerIds[0], StringComparison.Ordinal))
                {
                    return Task.FromResult(NodeResult.Failure("test.failed", "First PC failed."));
                }
                return Task.FromResult(ValueMergeFixture.Result(NodeValue.FromObject(request.TargetPeerId)));
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        WorkflowDefinition definition = Definition(Node(source) with { Target = new(EExecutionScope.All, peerIds) });
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), source).ConfigureAwait(true);
        if (firstFails)
        {
            Assert.Equal(EOutcome.Failed, report.Outcome);
            Assert.Equal("b", Assert.Single(received));
        }
        else
        {
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            Assert.Equal(peerIds, received.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>先のPCの動的な型不一致が、次のPCの正常入力を落とさないことを確認します。</summary>
    /// <returns>不正入力の明示的な失敗と、正常な後続一回です。</returns>
    [Fact]
    public async Task InvalidExtractionDoesNotDiscardOtherTargetArrivalsAsync()
    {
        List<string> received = [];
        string source = "source", firstPeer = "a";
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            received.Add(context.Inputs["value"].GetString()!);
            return ValueTask.FromResult(NodeResult.Success());
        });
        WorkflowFixture fixture = new(probe, new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Peers.Others = [new PeerInfo("b", Guid.NewGuid(), "host-b", "127.0.0.2", 0, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow)];
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, source, StringComparison.Ordinal))
            {
                NodeValue value = NodeValue.FromObject(new { name = "second" });
                if (string.Equals(request.TargetPeerId, firstPeer, StringComparison.Ordinal))
                {
                    value = NodeValue.FromBytes(new byte[] { 1, 2 });
                }
                return Task.FromResult(ValueMergeFixture.Result(value));
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        WorkflowDefinition definition = Definition(Node(source) with { Target = new(EExecutionScope.All, peerIds) });
        definition = definition with { Wires = [definition.Wires[0] with { Path = "/name" }, .. definition.Wires.Skip(1)] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), source).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Contains(report.Nodes.Single(node => node.NodeId is "merge").Targets, target => target.Result.ReasonCode is "input.path");
        Assert.Equal("second", Assert.Single(received));
    }

    /// <summary>反復の要素ごとに独立して合流し、通常の反復本体へ混ぜません。</summary>
    /// <returns>逐次・並列とも各要素が一度ずつ通る確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IterationItemsStartSeparateDownstreamExecutionsAsync(bool parallel)
    {
        ConcurrentQueue<int> received = new();
        ForEachNode loop = new(parallel);
        ValueMergeFixture.Probe sink = new((context, _) =>
        {
            received.Enqueue(context.Inputs["value"].Json.GetInt32());
            return ValueTask.FromResult(NodeResult.Success());
        });
        WorkflowFixture fixture = new(loop, sink, new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition source = Node("source", loop.Descriptor.Type) with { Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(elements) } };
        WorkflowDefinition definition = Definition(source) with { Wires = [new("source", "item", "merge", "input1"), new("merge", "start", "sink", "trigger"), new("merge", "value", "sink", "value")] };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), "source").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(elements, received.Order());
    }

    /// <summary>未実行や失敗の出力を正常到着として扱わず、明示した失敗経路だけを通します。</summary>
    /// <returns>経路に応じた後続数です。</returns>
    [Theory]
    [InlineData(EOutcome.Succeeded, "success", 1)]
    [InlineData(EOutcome.Failed, "success", 0)]
    [InlineData(EOutcome.Failed, "failed", 1)]
    [InlineData(EOutcome.NotExecuted, "*", 0)]
    public async Task OnlyActualMatchingResultsStartDownstreamAsync(EOutcome outcome, string route, int expected)
    {
        int calls = 0;
        string source = "source";
        ValueMergeFixture.Probe probe = new((context, _) =>
        {
            if (string.Equals(context.NodeId, source, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(ValueMergeFixture.Result(NodeValue.FromObject(42)) with { Outcome = outcome, Route = "failed" });
            }
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        });
        WorkflowFixture fixture = new(probe, new ValueMergeNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = Definition(Node(source));
        definition = definition with { Wires = [definition.Wires[0] with { When = route }, .. definition.Wires.Skip(1)] };
        _ = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition), source).ConfigureAwait(true);
        Assert.Equal(expected, calls);
    }

    /// <summary>通常の値出力を合流し、値と開始を同じ後続へ渡します。</summary>
    /// <returns>一件ずつ評価されるグラフです。</returns>
    private static WorkflowDefinition Definition(NodeDefinition source) => new("merged-values", "Merged values", [source, Node("merge", ValueMergePorts.NodeType), Node("sink")],
        [new("source", "value", "merge", "input1"), new("merge", "start", "sink", "trigger"), new("merge", "value", "sink", "value")]);

    /// <summary>空設定で有限ノードを追加します。</summary>
    /// <returns>ノード定義です。</returns>
    private static NodeDefinition Node(string id, string type = "test.value") => WorkflowFixture.Definition(id, type);
}
