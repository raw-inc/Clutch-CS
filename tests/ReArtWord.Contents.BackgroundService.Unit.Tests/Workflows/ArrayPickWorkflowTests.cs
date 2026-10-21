using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Arithmetic;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>実ランタイムで抽選結果の共有・要求分離・反復開始と設定口を検証します。</summary>
public sealed class ArrayPickWorkflowTests
{
    /// <summary>同じ抽選の二つの参照が一致し、別要求では新しい一回が実行されます。</summary>
    /// <returns>各要求につき一つの抽選結果です。</returns>
    [Theory]
    [InlineData("data.randomPick")]
    [InlineData("math.random")]
    public async Task FanOutSharesOneDrawWithinEachRequestAsync(string type)
    {
        WorkflowFixture fixture = new(new RandomPickNode(), new RandomNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        int calls = 0;
        string drawId = "draw";
        string randomPickType = "data.randomPick";
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, drawId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref calls);
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        JsonElement settings = NodeSettings.Schema("{}");
        Dictionary<string, NodeValue>? inputs = null;
        if (string.Equals(type, randomPickType, StringComparison.Ordinal))
        {
            inputs = new() { ["value"] = NodeValue.FromJson(NodeSettings.Schema("[null,1,\"two\"]")) };
        }
        WorkflowDefinition document = new("flow", "Random fan-out", [new("draw", type, settings, inputs), new("left", "control.pass", settings), new("right", "control.pass", settings)],
            [new("draw", "value", "left", "value"), new("draw", "value", "right", "value")]);
        CompiledWorkflow compiled = IterationTests.Compile(fixture, document);
        Assert.Equal(0, calls);
        ExecutionReport[] reports = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => fixture.Engine.ExecuteAsync(compiled))).ConfigureAwait(true);
        Assert.Equal(6, calls);
        foreach (ExecutionReport report in reports)
        {
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            NodeValue left = Assert.Single(report.Nodes.Single(node => node.NodeId is "left").Targets).Result.Outputs["value"];
            NodeValue right = Assert.Single(report.Nodes.Single(node => node.NodeId is "right").Targets).Result.Outputs["value"];
            Assert.True(JsonElement.DeepEquals(left.Json, right.Json));
        }
    }

    /// <summary>反復Indexで範囲設定とPickを供給し、要素開始で固定候補も毎回抽選します。</summary>
    /// <returns>各反復に対応した三列の結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectedBoundsAndExplicitStartsWorkPerElementAsync(bool parallel)
    {
        WorkflowFixture fixture = new(new RandomNode(), new RandomPickNode(), new PickNode(), new ForEachNode(), new ForEachNode(true), new ForEachEndNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        int draws = 0;
        string drawId = "draw";
        fixture.Dispatcher.Handler = (request, token) =>
        {
            if (string.Equals(request.Node.Id, drawId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref draws);
            }
            return fixture.Executor.ExecuteAsync(request, token);
        };
        string loopType = "control.forEach";
        if (parallel)
        {
            loopType = "control.parallelForEach";
        }
        JsonElement settings = NodeSettings.Schema("{}");
        NodeValue array = NodeValue.FromJson(NodeSettings.Schema("[\"one\",\"two\",\"three\"]"));
        WorkflowDefinition document = new("flow", "Random loop", [
            new("each", loopType, settings, new Dictionary<string, NodeValue> { ["value"] = array }),
            new("pick", "data.pick", settings, new Dictionary<string, NodeValue> { ["value"] = array }),
            new("draw", "data.randomPick", settings, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromJson(NodeSettings.Schema("[\"fixed\"]")) }),
            new("number", "math.random", settings), new("end", "control.forEach.end", settings)],
            [new("each", "loop", "end", "loop"), new("each", "index", "pick", "index"), new("each", "start", "draw", "trigger"),
                new("each", "index", "number", "$settings.minimum"), new("each", "index", "number", "$settings.maximum"),
                new("pick", "value", "end", "value"), new("draw", "value", "end", "value2"), new("number", "value", "end", "value3")]);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, document)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(3, draws);
        IReadOnlyDictionary<string, NodeValue> outputs = IterationResultTests.Outputs(report);
        Assert.Equal(array.Json.GetRawText(), outputs["value"].Json.GetRawText());
        Assert.Equal("[\"fixed\",\"fixed\",\"fixed\"]", outputs["value2"].Json.GetRawText());
        Assert.Equal("[0,1,2]", outputs["value3"].Json.GetRawText());
    }

    /// <summary>実行せずに固定Index、同種配列、動的モードの型を解決できます。</summary>
    /// <returns>保存・表示の型推測結果です。</returns>
    [Fact]
    public async Task InfersChosenTypesWithoutDrawingAsync()
    {
        WorkflowFixture fixture = new(new PickNode(), new RandomPickNode(), new RandomNode(), new ConstantValueNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        JsonElement settings = NodeSettings.Schema("{}");
        NodeValue mixed = NodeValue.FromJson(NodeSettings.Schema("[1,\"text\"]"));
        NodeValue paths = NodeValue.FromJson(NodeSettings.Schema("[\"a\",\"b\"]")) with { DataType = new("array", new("path")) };
        WorkflowDefinition document = new("flow", "Types", [
            new("pick", "data.pick", settings, new Dictionary<string, NodeValue> { ["value"] = mixed, ["index"] = NodeValue.FromObject(1) }),
            new("paths", "data.randomPick", settings, new Dictionary<string, NodeValue> { ["value"] = paths }),
            new("integer", "math.random", settings), new("decimal", "math.random", NodeSettings.Schema("{\"mode\":\"decimal\"}")),
            new("mode", "value.constant", NodeSettings.Schema("{\"text\":\"decimal\"}")), new("connected", "math.random", settings)],
            [new("mode", "value", "connected", "$settings.mode")]);
        WorkflowTypeInference inference = new(document, fixture.Registry.Descriptors);
        Assert.Equal("string", inference.PortType("pick", "value", true)!.Type);
        Assert.Equal("path", inference.PortType("paths", "value", true)!.Type);
        Assert.Equal("integer", inference.PortType("integer", "value", true)!.Type);
        Assert.Equal("number", inference.PortType("decimal", "value", true)!.Type);
        Assert.Equal("number", inference.PortType("connected", "value", true)!.Type);
    }
}
