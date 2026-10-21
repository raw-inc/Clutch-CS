using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>設定の適用条件と、実行結果から独立した値出力を検証します。</summary>
public sealed class NodeUsabilityTests
{
    /// <summary>条件の真偽値と実行結果オブジェクトを別々の配線から取得します。</summary>
    /// <returns>実エンジンの配線解決を通した確認です。</returns>
    [Fact]
    public async Task ConditionPublishesMatchedWithoutReplacingTheCommonResultAsync()
    {
        ConcurrentDictionary<string, NodeValue> received = new(StringComparer.Ordinal);
        WorkflowFixture.ActionNode sink = new((context, _) =>
        {
            received[context.NodeId] = context.Inputs["value"];
            return ValueTask.FromResult(NodeResult.Success());
        }, true);
        WorkflowFixture fixture = new(new ConditionNode(), sink);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        NodeDefinition condition = new("condition", "control.condition", JsonSerializer.SerializeToElement(new { operation = "boolean" }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("payload"), ["comparison"] = NodeValue.FromObject(true) });
        CompilationResult compiled = fixture.Compiler.Compile(new("condition-values", "Condition values", [condition, WorkflowFixture.Definition("matched"), WorkflowFixture.Definition("report")],
            [new("condition", "matched", "matched", "value", "true"), new("condition", "result", "report", "value", "true")]));
        Assert.Empty(compiled.Issues);
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.True(received["matched"].Json.GetBoolean());
        Assert.Equal(JsonValueKind.Object, received["report"].Json.ValueKind);
        Assert.True(received["report"].Json.TryGetProperty("Result", out _));
    }

    /// <summary>使わないモードの保存値が現在の操作を妨げず、切替後は検証されます。</summary>
    [Fact]
    public void ExtractValidatesOnlyTheSelectedModesFields()
    {
        ExtractNode node = new();
        JsonElement settings = JsonSerializer.Deserialize<JsonElement>("""{"mode":"pointer","path":"/name","fields":[{"name":"bad","type":"utf8","offset":0,"length":-1}],"pattern":"["}""");
        NodeDefinition definition = new("extract", node.Descriptor.Type, settings);
        Assert.Empty(node.Validate(definition));
        Assert.NotEmpty(node.Validate(definition with { Settings = JsonSerializer.Deserialize<JsonElement>("""{"mode":"binary","fields":[{"name":"bad","type":"utf8","offset":0,"length":-1}]}""") }));
    }

    /// <summary>毎日指定では古い曜日指定を使わず、曜日指定へ切り替えると検証します。</summary>
    [Fact]
    public void DailyScheduleDoesNotReadInactiveWeekdayValues()
    {
        ScheduleNode node = new();
        Assert.Empty(node.Validate(new("daily", node.Descriptor.Type, NodeSettings.Schema("""{"mode":"daily","time":"12:00","weekdays":["old"]}"""))));
        Assert.NotEmpty(node.Validate(new("weekly", node.Descriptor.Type, NodeSettings.Schema("""{"mode":"weekdays","time":"12:00","weekdays":["old"]}"""))));
    }
}
