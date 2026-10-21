using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>抽選の実行回数を観測し、正当な連続当選を不具合と判定せず共有範囲を確認します。</summary>
public sealed class RandomPickScopeTests
{
    /// <summary>候補が同じでも別受信は毎回抽選し、一件の二つの参照は同じ値を共有します。</summary>
    /// <returns>同一配列を繰り返し受信した場合の抽選回数です。</returns>
    [Fact]
    public async Task RepeatedIdenticalSourceEventsEachDrawOnceAsync()
    {
        WorkflowFixture fixture = new(new SettingsSourceProbe(), new CommentNode(), new RandomPickNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
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
        JsonElement empty = NodeSettings.Schema("{}");
        WorkflowDefinition definition = new("random-events", "Random events", [new("source", "test.settingSource", NodeSettings.Schema("{\"port\":1}")),
            new("comment", "value.comment", empty), new("draw", "data.randomPick", empty), new("left", "control.pass", empty), new("right", "control.pass", empty)],
            [new("source", "value", "comment", "value"), new("comment", "value", "draw", "value"), new("draw", "value", "left", "value"), new("draw", "value", "right", "value")]);
        CompiledWorkflow compiled = IterationTests.Compile(fixture, definition);
        Assert.Equal(0, draws);
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), definition.Id, compiled.Revision, "source", "a", definition.Nodes[0].Settings,
            new Dictionary<string, NodeValue>(), TimeProvider.System);
        NodeResult values = NodeResult.Success(NodeValue.FromJson(NodeSettings.Schema("[\"alpha\",\"beta\",\"gamma\",\"delta\"]")));
        for (int index = 0; index < 32; index++)
        {
            context = context with { ExecutionId = Guid.NewGuid(), CausationId = Guid.NewGuid() };
            ExecutionReport report = await fixture.Engine.ExecuteEmissionAsync(compiled, new(context, values)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            NodeValue left = Assert.Single(report.Nodes.Single(node => node.NodeId is "left").Targets).Result.Outputs["value"];
            NodeValue right = Assert.Single(report.Nodes.Single(node => node.NodeId is "right").Targets).Result.Outputs["value"];
            Assert.Equal(left.GetString(), right.GetString());
            Assert.Equal(index + 1, draws);
        }
    }

    /// <summary>反復外の抽選は一度だけ、開始接続した抽選は各要素で実行し、別WF実行へ持ち越しません。</summary>
    /// <returns>音声候補の辞書を加工してForEachへ渡す配線の再現です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitElementStartControlsWhetherAChosenVoiceIsSharedAsync(bool parallel, bool perElement)
    {
        WorkflowFixture fixture = new(new ButtonNode(), new CommentNode(), new RandomPickNode(), new KeyValuePartNode(false), new KeyValueNode(), new ForEachNode(parallel), new ForEachEndNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
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
        JsonElement empty = NodeSettings.Schema("{}");
        string loop = "control.forEach";
        int expected = 1;
        if (parallel)
        {
            loop = "control.parallelForEach";
        }
        List<WireDefinition> wires = [new("button", "value", "comment", "value"), new("comment", "value", "draw", "value"),
            new("draw", "value", "voice", "value"), new("button", "start", "loop", "trigger"), new("loop", "item", "pair", "key"),
            new("voice", "value", "pair", "value"), new("loop", "loop", "end", "loop"), new("pair", "value", "end", "value")];
        if (perElement)
        {
            wires.Add(new("loop", "start", "draw", "trigger"));
            expected = 3;
        }
        WorkflowDefinition definition = new("voices", "Voice selection", [new("button", "control.button", empty), new("comment", "value.comment", empty),
            new("draw", "data.randomPick", empty), new("voice", "data.keyValue.value", empty), new("pair", "value.keyValue", empty),
            new("loop", loop, empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromJson(NodeSettings.Schema("[\"one\",\"two\",\"three\"]")) }),
            new("end", "control.forEach.end", empty)], wires);
        CompiledWorkflow compiled = IterationTests.Compile(fixture, definition);
        for (int index = 0; index < 2; index++)
        {
            ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled, "button", new Dictionary<string, NodeValue>
            {
                ["value"] = NodeValue.FromJson(NodeSettings.Schema("{\"A\":\"alpha\",\"B\":\"beta\",\"C\":\"gamma\"}")),
            }).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, report.Outcome);
            Assert.Equal(expected * (index + 1), draws);
            JsonElement output = IterationResultTests.Outputs(report)["value"].Json;
            Assert.Equal(3, output.EnumerateObject().Count());
            if (!perElement)
            {
                Assert.Single(output.EnumerateObject().Select(pair => pair.Value.GetString()).Distinct());
            }
        }
    }
}
