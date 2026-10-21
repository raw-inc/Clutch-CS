using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.State;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>要素内の値合流を、並列要素・別要求・遅い後続から分離して検証します。</summary>
public sealed class IterationValueMergeTests
{
    /// <summary>排他枝の値を同じ要素へ戻し、先行要求が待機していても別要求を混ぜません。</summary>
    /// <returns>元の要求識別子、各要素の値、Endの収集と配送回数の確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BranchValuesStayWithTheirElementAndRequestAsync(bool parallel, bool signal)
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentBag<(string Request, int Index, Guid Execution)> received = [];
        ConcurrentBag<string> branches = [];
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        string left = "left", right = "right", firstRequest = "first";
        WorkflowFixture fixture = Create(async (context, token) =>
        {
            JsonElement value = context.Inputs["value"].Json;
            string request = value.GetProperty("request").GetString()!;
            int index = value.GetProperty("index").GetInt32();
            bool selected = value.GetProperty("selected").GetBoolean();
            if (string.Equals(context.NodeId, left, StringComparison.Ordinal) || string.Equals(context.NodeId, right, StringComparison.Ordinal))
            {
                Assert.Equal(selected, context.NodeId is "left");
                branches.Add(request);
                return NodeResult.Success(context.Inputs["value"].Copy());
            }
            Assert.Equal(index, context.Inputs["number"].Json.GetInt32());
            Assert.Equal(request, context.Inputs["address"].GetString());
            Guid expected = secondId;
            bool isFirst = string.Equals(request, firstRequest, StringComparison.Ordinal);
            if (isFirst)
            {
                expected = firstId;
            }
            Assert.Equal(expected, context.CausationId);
            if (isFirst && index == 0)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            received.Add((request, index, context.ExecutionId));
            return NodeResult.Success(NodeValue.FromObject(request + ":" + index.ToString(CultureInfo.InvariantCulture)));
        }, parallel);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Engine.RetainedValues = new TestStateStore();
        CompiledWorkflow compiled = ValueMergeFixture.Compile(fixture, Definition(parallel, signal));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<ExecutionReport> first = fixture.Engine.ExecuteAsync(compiled, "button", Input("first"), firstId, timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            ExecutionReport second = await fixture.Engine.ExecuteAsync(compiled, "button", Input("second"), secondId, timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, second.Outcome);
            Assert.Equal(4, received.Count(item => item.Request is "second"));
            Assert.False(first.IsCompleted);
            Assert.Equal(Enumerable.Range(0, 4).Select(index => "second:" + index.ToString(CultureInfo.InvariantCulture)), Values(second));
        }
        finally
        {
            release.TrySetResult();
            await first.ConfigureAwait(true);
        }
        ExecutionReport report = await first.ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(Enumerable.Range(0, 4).Select(index => "first:" + index.ToString(CultureInfo.InvariantCulture)), Values(report));
        Assert.Equal(8, received.Count);
        Assert.Equal(8, received.Select(item => item.Execution).Distinct().Count());
        Assert.Equal(4, branches.Count(request => request is "first"));
        Assert.Equal(4, branches.Count(request => request is "second"));
        Assert.Empty(fixture.Engine.Active());
    }

    /// <summary>入力ごとに同じ配線の実ノードを生成します。</summary>
    /// <returns>値以外の処理と反復制御を実装へ委ねる構成です。</returns>
    internal static WorkflowFixture Create(Func<Runtime.Nodes.NodeExecutionContext, CancellationToken, ValueTask<NodeResult>> handler, bool parallel)
        => new(new ButtonNode(), new ForEachNode(parallel), new ForEachEndNode(), new IfNode(), new IfNode(true), new ValueMergeNode(), new ValueMergeFixture.Probe(handler));

    /// <summary>二つの枝を合流し、同じ要素からの引数と一緒に後続へ渡します。</summary>
    /// <returns>音声選択の配線を外部APIなしで再現したグラフです。</returns>
    internal static WorkflowDefinition Definition(bool parallel, bool signal)
    {
        string loop = IterationNodeTypes.ForEach;
        if (parallel)
        {
            loop = IterationNodeTypes.ParallelForEach;
        }
        List<WireDefinition> wires = [new("button", "start", "loop", "trigger"), new("button", "value/items", "loop", "value"),
            new("loop", "item/selected", "yes", "condition"), new("loop", "item/selected", "no", "condition"),
            new("yes", "start", "left", "trigger"), new("no", "start", "right", "trigger"), new("loop", "item", "left", "value"), new("loop", "item", "right", "value"),
            new("left", "value", "merge", "input1"), new("right", "value", "merge", "input2"), new("merge", "value", "sink", "value"),
            new("loop", "index", "sink", "number"), new("button", "value/request", "sink", "address"), new("loop", "loop", "end", "loop"), new("sink", "value", "end", "value")];
        if (signal)
        {
            wires.Add(new("merge", "start", "sink", "trigger"));
        }
        return new("element-values", "Element values", [Node("button", "control.button"), Node("loop", loop), Node("yes", "control.ifStart"), Node("no", "control.ifNotStart"),
            Node("left"), Node("right"), Node("merge", ValueMergePorts.NodeType), Node("sink"), Node("end", IterationNodeTypes.End)], wires);
    }

    /// <summary>同じ値が繰り返されても別要素として識別する入力です。</summary>
    /// <returns>要求識別と4件の排他条件です。</returns>
    internal static Dictionary<string, NodeValue> Input(string request) => new(StringComparer.Ordinal)
    {
        ["value"] = NodeValue.FromObject(new { request, items = Enumerable.Range(0, 4).Select(index => new { request, index, selected = index % 2 == 0, text = "same" }).ToArray() }),
    };

    /// <summary>Endで確定した各要素の結果を読みます。</summary>
    /// <returns>入力順の集約文字列です。</returns>
    private static IEnumerable<string?> Values(ExecutionReport report) => Assert.Single(report.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs["value"].Json.EnumerateArray().Select(item => item.GetString());

    /// <summary>既定設定のノードを配置します。</summary>
    /// <returns>静的な保存定義です。</returns>
    private static NodeDefinition Node(string id, string type = "test.value") => WorkflowFixture.Definition(id, type);
}
