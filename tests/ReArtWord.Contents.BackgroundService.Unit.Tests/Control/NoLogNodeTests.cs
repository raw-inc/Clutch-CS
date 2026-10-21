using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>記録無効化は正常な値の引継ぎと後続Signalを維持します。</summary>
public sealed class NoLogNodeTests
{
    /// <summary>記録を停止しても同じ入力が後続ノードへ届きます。</summary>
    /// <returns>実コンパイラーと実行エンジンを通した確認です。</returns>
    [Fact]
    public async Task DisabledLogPassesValueAndStartsSuccessor()
    {
        ObservationSink sink = new();
        WorkflowFixture fixture = new(new WriteLogNode(sink));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition disabled = new("disabled", "control.log", JsonSerializer.SerializeToElement(new { level = "NOLOG", message = "never recorded", personal = true }),
            Inputs: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(new { number = 42 }) });
        NodeDefinition after = new("after", "control.log", JsonSerializer.SerializeToElement(new { message = "after" }));
        WorkflowDefinition definition = new("no-log", "no-log", [disabled, after], [new("disabled", "$completed", "after", "trigger"), new("disabled", "value", "after", "value")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Engine.ExecuteAsync(compiled.Workflow!)).Outcome);
        WorkflowLogEntry written = Assert.Single(sink.Logs);
        Assert.Equal("after", written.Message);
        Assert.Equal(42, written.Data!.Json.GetProperty("number").GetInt32());
    }

    /// <summary>未指定の既定レベルと明示した通常レベルは従来通り記録します。</summary>
    /// <returns>記録有無の確認です。</returns>
    [Theory]
    [InlineData("{}", "INFO")]
    [InlineData("{\"level\":\"ERROR\"}", "ERROR")]
    public async Task ExistingLevelsContinueWriting(string settings, string expected)
    {
        ObservationSink sink = new();
        Assert.Equal(EOutcome.Succeeded, (await new WriteLogNode(sink).ExecuteAsync(NodeTestContext.Create(settings))).Outcome);
        Assert.Equal(expected, Assert.Single(sink.Logs).Level);
    }
}
