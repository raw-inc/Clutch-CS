using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Targets;

/// <summary>反復内のPC候補の合流を、要素ごとの条件と同じ範囲で解決します。</summary>
public sealed class IterationPcValueMergeTests
{
    /// <summary>異なる要素の実行PCを取り違えず、選択候補を混合しません。</summary>
    /// <returns>逐次・並列それぞれの宛先確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachElementSelectsItsOwnExecutionPcAsync(bool parallel)
    {
        ConcurrentBag<(int Index, string Peer)> observed = [];
        WorkflowFixture fixture = new(new ButtonNode(), new ForEachNode(parallel), new ForEachEndNode(), new IfNode(), new IfNode(true), new PcSelectionNode(), new ValueMergeNode(),
            new ValueMergeFixture.Probe((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Peers.Others = [new("b", Guid.NewGuid(), "host-b", "127.0.0.1", 1, EPeerStatus.Ready, 900, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)];
        string sink = "sink";
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, sink, StringComparison.Ordinal))
            {
                int index = request.Inputs["number"].Json.GetInt32();
                observed.Add((index, request.TargetPeerId));
                return Task.FromResult(NodeResult.Success(NodeValue.FromObject(request.TargetPeerId)));
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        WorkflowDefinition definition = IterationValueMergeTests.Definition(parallel, true);
        NodeDefinition[] nodes = definition.Nodes.Select(node => node.Id switch
        {
            "left" => node with { Type = PcSelectionPorts.SelectNode, Settings = NodeSettings.Schema("""{"mode":"selected","hostIds":["host-a"]}""") },
            "right" => node with { Type = PcSelectionPorts.SelectNode, Settings = NodeSettings.Schema("""{"mode":"selected","hostIds":["host-b"]}""") },
            _ => node,
        }).ToArray();
        List<WireDefinition> wires = definition.Wires.Where(wire => !(wire.ToNode is "left" or "right" && wire.ToPort is "value")
            && !(wire.FromNode is "merge" && wire.ToPort is "value")).ToList();
        wires.AddRange([new("merge", "value", "sink", PcSelectionPorts.Execution), new("loop", "item", "sink", "value")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, definition with { Nodes = nodes, Wires = wires }), "button", IterationValueMergeTests.Input("pc")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(4, observed.Count);
        Assert.Equal("[\"a\",\"b\",\"a\",\"b\"]", Assert.Single(report.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs["value"].Json.GetRawText());
    }
}
