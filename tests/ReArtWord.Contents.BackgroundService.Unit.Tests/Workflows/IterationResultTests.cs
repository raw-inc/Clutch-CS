using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control.Iteration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>複数の収集列が、並列要素・別要求・空入力でも同じ組と順序を保つことを検証します。</summary>
public sealed class IterationResultTests
{
    /// <summary>別要求も同時に実行し、完成順を変えても各列の添字と値が混ざらないことを確認します。</summary>
    /// <returns>独立した三要求の複数列です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleColumnsPreserveElementAndRequestIdentityAsync(bool parallel)
    {
        WorkflowFixture fixture = new(new ForEachNode(), new ForEachNode(true), new ForEachEndNode(), new IterationControlTestNode(async (context, token) =>
        {
            int value = context.Inputs["value"].Json.GetInt32();
            await Task.Delay(20 - value % 3 * 5, token).ConfigureAwait(false);
            return IterationControlTestNode.Result(value, value % 2 == 0);
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition document = Definition(parallel);
        CompiledWorkflow compiled = IterationTests.Compile(fixture, document);
        int[][] groups = [[11, 12, 13], [21, 22, 23], [31, 32, 33]];
        ExecutionReport[] reports = await Task.WhenAll(groups.Select(values => fixture.Engine.ExecuteAsync(compiled, "each",
            new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(values) }))).ConfigureAwait(true);
        for (int group = 0; group < groups.Length; group++)
        {
            Assert.Equal(EOutcome.Succeeded, reports[group].Outcome);
            IReadOnlyDictionary<string, NodeValue> outputs = Outputs(reports[group]);
            Assert.Equal(groups[group], outputs["value"].Json.EnumerateArray().Select(item => item.GetInt32()));
            Assert.Equal(groups[group].Select(value => "item-" + value.ToString(System.Globalization.CultureInfo.InvariantCulture)), outputs["value2"].Json.EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(groups[group].Select(value => value % 2 == 0), outputs["value3"].Json.EnumerateArray().Select(item => item.GetBoolean()));
            Assert.Equal([0, 1, 2], IterationTests.Loop(reports[group]).Iteration!.Items.Select(item => item.Index));
        }
        Assert.Equal(3, reports.Select(report => report.Id).Distinct().Count());
    }

    /// <summary>空配列は各使用列で空配列を返し、出力だけの未配線欄は要素ごとにnullになります。</summary>
    /// <returns>空入力と、欠番を詰めない列の確認です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyInputAndOutputOnlyPortsKeepAllColumnShapesAsync(bool empty)
    {
        int calls = 0;
        WorkflowFixture fixture = new(new ForEachNode(), new ForEachEndNode(), new IterationControlTestNode((context, _) =>
        {
            calls++;
            return ValueTask.FromResult(IterationControlTestNode.Result(context.Inputs["value"].Json.GetInt32()));
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        string json = "[]";
        int expected = 0;
        if (!empty)
        {
            json = "[1,2]";
            expected = 2;
        }
        WorkflowDefinition source = Definition(false, json);
        NodeDefinition end = source.Nodes.Single(node => node.Id is "end") with { Inputs = new Dictionary<string, NodeValue> { ["value4"] = NodeValue.Null } };
        WorkflowDefinition document = source with { Nodes = source.Nodes.Select(node =>
        {
            if (string.Equals(node.Id, end.Id, StringComparison.Ordinal))
            {
                return end;
            }
            return node;
        }).ToArray() };
        ExecutionReport report = await fixture.Engine.ExecuteAsync(IterationTests.Compile(fixture, document)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Equal(expected, calls);
        foreach (NodeValue value in Outputs(report).Values)
        {
            Assert.Equal(expected, value.Json.GetArrayLength());
        }
        Assert.All(Outputs(report)["value4"].Json.EnumerateArray(), value => Assert.Equal(System.Text.Json.JsonValueKind.Null, value.ValueKind));
    }

    /// <summary>入力順の三列を構成する、通常の保存定義です。</summary>
    /// <returns>旧valueと追加value2/value3を使うグラフです。</returns>
    internal static WorkflowDefinition Definition(bool parallel, string json = "[1,2,3]")
    {
        WorkflowDefinition basic = IterationTests.Definition(json, parallel);
        string body = "body";
        return basic with
        {
            Nodes = basic.Nodes.Select(node =>
            {
                if (string.Equals(node.Id, body, StringComparison.Ordinal))
                {
                    return node with { Type = "test.iteration" };
                }
                return node;
            }).ToArray(),
            Wires = [.. basic.Wires, new("body", "text", "end", "value2"), new("body", "condition", "end", "value3")],
        };
    }

    /// <summary>外側へ公開した一つのEndだけを参照します。</summary>
    /// <returns>列ごとの集約配列です。</returns>
    internal static IReadOnlyDictionary<string, NodeValue> Outputs(ExecutionReport report)
        => Assert.Single(report.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs;
}
