using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>文字列変換の書式と、書式を適用しない既存変換の境界を確認します。</summary>
public sealed class ScalarFormatTests
{
    /// <summary>ホストのカルチャーによらず、数値の桁を維持して明示書式を使います。</summary>
    /// <returns>ゼロ埋め、桁区切り、小数、指数、整数精度の確認です。</returns>
    [Theory]
    [InlineData("42", "D6", "000042")]
    [InlineData("42.0", "D4", "0042")]
    [InlineData("-42", "D4", "-0042")]
    [InlineData("1234567", "N0", "1,234,567")]
    [InlineData("1234.567", "N2", "1,234.57")]
    [InlineData("1.236", "F2", "1.24")]
    [InlineData("12.3", "0000.00", "0012.30")]
    [InlineData("1234.5", "#,##0.00", "1,234.50")]
    [InlineData("0.125", "P1", "12.5 %")]
    [InlineData("9007199254740993", "N0", "9,007,199,254,740,993")]
    [InlineData("0.1234567890123456789", "F19", "0.1234567890123456789")]
    [InlineData("1e-29", "E2", "1.00E-029")]
    [InlineData("1.2300", "", "1.2300")]
    [InlineData("\"0042\"", "D8", "0042")]
    [InlineData("true", "", "true")]
    public async Task FormatsAreInvariantAndPreserveStringInputsAsync(string input, string format, string expected)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            NodeResult result = await ExecuteAsync(input, NodeValue.FromObject(format)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(expected, result.Outputs["value"].GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>不正書式・過剰な精度・非数値を未書式の正常結果に読み替えません。</summary>
    /// <returns>コード付きの失敗と出力なしを確認します。</returns>
    [Theory]
    [InlineData("42", "\"Q2\"", "data.convert.format.invalid")]
    [InlineData("42", "\"F999999999\"", "data.convert.format.range")]
    [InlineData("42", "\"F1025\"", "data.convert.format.range")]
    [InlineData("42", "\"F999999999\\u0000\"", "data.convert.format.range")]
    [InlineData("42", "123", "data.convert.format.type")]
    [InlineData("true", "\"F2\"", "data.convert.format.unsupported")]
    [InlineData("1.25", "\"D4\"", "data.convert.integer.fractional")]
    [InlineData("1e999", "\"F2\"", "data.convert.number.range")]
    public async Task InvalidFormatsFailWithoutOutputAsync(string input, string format, string code)
    {
        NodeResult result = await ExecuteAsync(input, NodeValue.FromJson(NodeSettings.Schema(format))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(code, result.ReasonCode);
        Assert.Empty(result.Outputs);
    }

    /// <summary>通常の配線から書式を供給し、文字列変換後に正しい結果を取得します。</summary>
    /// <returns>コンパイル・入力解決・実行までの確認です。</returns>
    [Fact]
    public async Task FormatCanBeSuppliedByAnotherNodeAsync()
    {
        WorkflowFixture fixture = new(new ConstantValueNode(), new ConvertNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition document = new("format", "Numeric format", [
            new("number", "value.constant", NodeSettings.Schema("""{"type":"integer","text":"42"}""")),
            new("format", "value.constant", NodeSettings.Schema("""{"type":"string","text":"D4"}""")),
            new("convert", "data.convert", NodeSettings.Schema("""{"targetType":"string"}""")),
        ], [new("number", "value", "convert", "value"), new("format", "value", "convert", "format")]);
        CompilationResult compiled = fixture.Compiler.Compile(document);
        Assert.Empty(compiled.Issues);
        WorkflowDefinition missing = document with { Wires = [document.Wires[1]] };
        Assert.Contains(fixture.Compiler.Compile(missing).Issues, issue => issue.Code is "input.required" && issue.NodeId is "convert");
        ExecutionReport report = await fixture.Engine.ExecuteAsync(compiled.Workflow!).ConfigureAwait(true);
        NodeResult converted = Assert.Single(report.Nodes.Single(node => node.NodeId is "convert").Targets).Result;
        Assert.Equal(EOutcome.Succeeded, converted.Outcome);
        Assert.Equal("0042", converted.Outputs["value"].GetString());
    }

    /// <summary>文字列以外への変換とPathの文字列化には数値書式を適用しません。</summary>
    /// <returns>非適用時の元の変換結果です。</returns>
    [Fact]
    public async Task FormatsDoNotChangeOtherConversionsAsync()
    {
        NodeResult number = await new ConvertNode().ExecuteAsync(NodeTestContext.Create("""{"targetType":"integer"}""", NodeValue.FromObject("42"),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["format"] = NodeValue.FromObject("N0") })).ConfigureAwait(true);
        Assert.Equal(42, number.Outputs["value"].Json.GetInt64());
        NodeResult path = await new ConvertNode().ExecuteAsync(NodeTestContext.Create("""{"targetType":"string"}""", Contracts.Values.PathValues.Create("folder/file"),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["format"] = NodeValue.FromObject("D4") })).ConfigureAwait(true);
        Assert.Equal("folder/file", path.Outputs["value"].GetString());
        Assert.Null(path.Outputs["value"].DataType);
    }

    /// <summary>変換ノードの公開入力に書式を渡します。</summary>
    /// <returns>ノードの実行結果です。</returns>
    private static ValueTask<NodeResult> ExecuteAsync(string input, NodeValue format) => new ConvertNode().ExecuteAsync(NodeTestContext.Create("""{"targetType":"string"}""",
        NodeValue.FromJson(NodeSettings.Schema(input)), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["format"] = format }));
}
