using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>ログ本文の実行時入力と従来の設定を検証します。</summary>
public sealed class LogMessageInputTests
{
    /// <summary>実行時の文字列・JSON値を精度損失なく本文に使い、付随値は維持します。</summary>
    /// <returns>記録結果の確認です。</returns>
    [Theory]
    [InlineData("\"text\"", "text")]
    [InlineData("\"\"", "")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("1.2345678901234567890123456789", "1.2345678901234567890123456789")]
    [InlineData("-1.5e-12", "-1.5e-12")]
    [InlineData("null", "null")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("{}", "{}")]
    [InlineData("[]", "[]")]
    [InlineData("{\"address\":\"/audio/request\",\"arguments\":[{\"type\":\"s\",\"value\":\"03\"}]}", "{\"address\":\"/audio/request\",\"arguments\":[{\"type\":\"s\",\"value\":\"03\"}]}")]
    [InlineData("[9007199254740993,{\"text\":\"日本語\",\"missing\":null}]", "[9007199254740993,{\"text\":\"日本語\",\"missing\":null}]")]
    public async Task RuntimeMessageOverridesFallbackAndPreservesData(string json, string expected)
    {
        ObservationSink sink = new();
        NodeValue value = NodeValue.FromObject("attached data");
        NodeExecutionContext context = NodeTestContext.Create("""{"message":"fallback","personal":true}""", value,
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["message"] = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json)) });
        NodeResult result = await new WriteLogNode(sink).ExecuteAsync(context);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(value.Json.GetRawText(), result.Outputs["value"].Json.GetRawText());
        Assert.Equal(expected, Assert.Single(sink.Logs).Message);
        Assert.Equal(value.Json.GetRawText(), sink.Logs[0].Data!.Json.GetRawText());
        Assert.Equal(context.CausationId, sink.Logs[0].CausationId);
        Assert.True(sink.Logs[0].ContainsPersonalData);
    }

    /// <summary>生バイナリーや未定義JSONは副作用を起こす前に拒否します。</summary>
    /// <returns>記録されなかったことの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedMessageDoesNotWrite(bool undefined)
    {
        ObservationSink sink = new();
        NodeValue message = NodeValue.FromBytes(new byte[] { 1 });
        if (undefined)
        {
            message = new(EValueKind.Json, default, default);
        }
        NodeResult result = await new WriteLogNode(sink).ExecuteAsync(NodeTestContext.Create(extra:
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["message"] = message }));
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Empty(sink.Logs);
    }

    /// <summary>通常配線の評価・実行から本文が届き、保存済みフォールバックも維持します。</summary>
    /// <returns>実コンパイラーと実行エンジンの検証です。</returns>
    [Theory]
    [InlineData("hello", "string")]
    [InlineData("42", "integer")]
    [InlineData("{\"address\":\"/audio/request\",\"arguments\":[\"03\"]}", "object")]
    [InlineData("[1,{\"value\":9007199254740993},null]", "array")]
    [InlineData("true", "boolean")]
    public async Task WiredMessageRunsThroughWorkflow(string text, string type)
    {
        ObservationSink sink = new();
        WorkflowFixture fixture = new(new ConstantValueNode(), new WriteLogNode(sink));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { text, type }));
        NodeDefinition target = new("log", "control.log", JsonSerializer.SerializeToElement(new { message = "fallback" }));
        WorkflowDefinition definition = new("logging", "logging", [source, target], [new("source", "value", "log", "message")]);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Engine.ExecuteAsync(compiled.Workflow!)).Outcome);
        Assert.Equal(text, Assert.Single(sink.Logs).Message);
        Assert.Equal("fallback", target.Settings.GetProperty("message").GetString());
        sink.Logs.Clear();
        await new WriteLogNode(sink).ExecuteAsync(NodeTestContext.Create("""{"message":"fallback"}"""));
        Assert.Equal("fallback", Assert.Single(sink.Logs).Message);
    }
}
