using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>柔軟な結合入力の順序と、暗黙変換しない型境界を検証します。</summary>
public sealed class ConcatNodeTests
{
    /// <summary>文字列と同型配列を順番通り結合し、空の値も正常に扱います。</summary>
    /// <returns>結合したJSON値の確認です。</returns>
    [Theory]
    [InlineData("\"前\"", "\"後\"", "\"前後\"")]
    [InlineData("\"\"", "\"x\"", "\"x\"")]
    [InlineData("[1,2]", "[3,4]", "[1,2,3,4]")]
    [InlineData("[]", "[1]", "[1]")]
    [InlineData("[]", "[]", "[]")]
    [InlineData("[1]", "[1.5]", "[1,1.5]")]
    [InlineData("[true]", "[false]", "[true,false]")]
    [InlineData("[null]", "[null]", "[null,null]")]
    [InlineData("[[],[1]]", "[[2,3]]", "[[],[1],[2,3]]")]
    [InlineData("[{\"age\":1}]", "[{\"age\":2}]", "[{\"age\":1},{\"age\":2}]")]
    [InlineData("[9007199254740993]", "[9223372036854775807]", "[9007199254740993,9223372036854775807]")]
    public async Task ConcatenatesWithoutChangingInputValues(string first, string second, string expected)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal) { ["input2"] = Value(second), ["input1"] = Value(first) };
        NodeResult result = await new ConcatNode().ExecuteAsync(NodeTestContext.Create(extra: inputs));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(Value(expected).Json.ToString(), result.Outputs["value"].Json.ToString());
        Assert.Equal(first, inputs["input1"].Json.GetRawText());
        Assert.Equal(second, inputs["input2"].Json.GetRawText());
    }

    /// <summary>入力単体や内部要素の異なる型を文字列化などで補正しません。</summary>
    /// <returns>出力を生成しない失敗の確認です。</returns>
    [Theory]
    [InlineData("\"text\"", "[1]")]
    [InlineData("1", "2")]
    [InlineData("null", "\"x\"")]
    [InlineData("[1]", "[\"2\"]")]
    [InlineData("[1,\"2\"]", "[]")]
    [InlineData("[{\"a\":1}]", "[{\"a\":\"2\"}]")]
    [InlineData("[{\"a\":1}]", "[{\"b\":2}]")]
    public async Task RejectsMismatchedTypes(string first, string second)
    {
        NodeResult result = await new ConcatNode().ExecuteAsync(NodeTestContext.Create(extra:
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input1"] = Value(first), ["input2"] = Value(second) }));
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
        Assert.StartsWith("value.concat.", result.ReasonCode, StringComparison.Ordinal);
    }

    /// <summary>辞書・接続の追加順や番号の文字列順に左右されません。</summary>
    /// <returns>番号順と途中の未接続口の扱いです。</returns>
    [Fact]
    public async Task NumericPortOrderIgnoresHolesAndTrigger()
    {
        NodeResult result = await new ConcatNode().ExecuteAsync(NodeTestContext.Create(extra:
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input10"] = NodeValue.FromObject("C"), ["trigger"] = NodeValue.FromObject("ignored"), ["input2"] = NodeValue.FromObject("B"), ["input1"] = NodeValue.FromObject("A") }));
        Assert.Equal("ABC", result.Outputs["value"].GetString());
        Assert.Equal(EOutcome.Failed, (await new ConcatNode().ExecuteAsync(NodeTestContext.Create())).Outcome);
    }

    /// <summary>出力の保持・型検証・ポート数の上限を越えて処理しません。</summary>
    [Fact]
    public void LimitsAreEnforcedWithoutPartialResults()
    {
        Assert.Throws<FormatException>(() => ConcatValues.Join([NodeValue.FromObject(new string('a', 1048576))]));
        Assert.Throws<FormatException>(() => ConcatValues.Join([NodeValue.FromObject(Enumerable.Range(0, 4097))]));
        Assert.Throws<FormatException>(() => ConcatValues.Join([NodeValue.FromBytes(new byte[] { 1 })]));
    }

    /// <summary>追加入力を含む実グラフが接続順に依存せず実行できます。</summary>
    /// <returns>コンパイル・実行・静的プレビューの一致です。</returns>
    [Fact]
    public async Task WorkflowAndStaticPreviewUseTheSameOrdering()
    {
        WorkflowFixture fixture = new(new ConstantValueNode(), new ConcatNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition[] nodes = [new("a", "value.constant", JsonSerializer.SerializeToElement(new { text = "A", type = "string" })),
            new("b", "value.constant", JsonSerializer.SerializeToElement(new { text = "B", type = "string" })),
            new("c", "data.concat", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["input10"] = NodeValue.FromObject("C") })];
        WorkflowDefinition document = new("concat", "concat", nodes, [new("b", "value", "c", "input2"), new("a", "value", "c", "input1")]);
        CompilationResult compiled = fixture.Compiler.Compile(document);
        Assert.Empty(compiled.Issues);
        ExecutionReport result = await fixture.Engine.ExecuteAsync(compiled.Workflow!);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("ABC", Assert.Single(Assert.Single(result.Nodes, node => string.Equals(node.NodeId, "c", StringComparison.Ordinal)).Targets).Result.Outputs["value"].GetString());
        Assert.Equal("ABC", new WorkflowSettingValues(document).Output("c")!.GetString());
    }

    /// <summary>数値の表現を変えずJSON値を作ります。</summary>
    /// <returns>元のJSON値です。</returns>
    private static NodeValue Value(string json) => NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json));
}
