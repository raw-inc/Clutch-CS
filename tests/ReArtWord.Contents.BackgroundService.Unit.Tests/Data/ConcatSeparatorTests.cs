using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>結合の任意区切りと、実行・静的解釈の一致を検証します。</summary>
public sealed class ConcatSeparatorTests
{
    /// <summary>未接続口だけを飛ばし、空文字の要素を含めて要素間だけに区切りを入れます。</summary>
    /// <returns>両端に余分な区切りを追加しない実行結果です。</returns>
    [Theory]
    [InlineData(", ", "A", "B", "C", "A, B, C")]
    [InlineData(" / ", "A", "", "C", "A /  / C")]
    [InlineData("と", "前", "中", "後", "前と中と後")]
    [InlineData("", "A", "B", "C", "ABC")]
    [InlineData("\r\n", "A", "B", "C", "A\r\nB\r\nC")]
    [InlineData(",", "", "", "", ",,")]
    public async Task InsertsSeparatorOnlyBetweenSuppliedElementsAsync(string separator, string first, string second, string third, string expected)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal)
        {
            ["input10"] = NodeValue.FromObject(third), ["separator"] = NodeValue.FromObject(separator),
            ["input1"] = NodeValue.FromObject(first), ["input3"] = NodeValue.FromObject(second),
        };
        NodeResult result = await new ConcatNode().ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].GetString());
        NodeDefinition node = new("concat", "data.concat", JsonSerializer.SerializeToElement(new { }), inputs);
        Assert.Equal(expected, new WorkflowSettingValues(new("test", "test", [node], [])).Output(node.Id)!.GetString());
        inputs.Remove("input10");
        inputs.Remove("input3");
        Assert.Equal(first, (await new ConcatNode().ExecuteAsync(NodeTestContext.Create(extra: inputs)).ConfigureAwait(true)).Outputs["value"].GetString());
    }

    /// <summary>省略と明示nullは空文字、配列へ区切り文字を混ぜる指定はエラーです。</summary>
    [Fact]
    public void DefaultNullAndArraysKeepTheirExistingMeaning()
    {
        NodeValue[] text = [NodeValue.FromObject("a"), NodeValue.FromObject("b")];
        Assert.Equal("ab", ConcatValues.Join(text).GetString());
        Assert.Equal("ab", ConcatValues.Join(text, NodeValue.Null).GetString());
        NodeValue[] arrays = [NodeValue.FromObject<int[]>([1]), NodeValue.FromObject<int[]>([2])];
        Assert.Equal("[1,2]", ConcatValues.Join(arrays, NodeValue.Null).Json.GetRawText());
        Assert.Equal("[1,2]", ConcatValues.Join(arrays, NodeValue.FromObject("")).Json.GetRawText());
        Assert.Throws<FormatException>(() => ConcatValues.Join(arrays, NodeValue.FromObject(",")));
        Assert.Throws<FormatException>(() => ConcatValues.Join(text, NodeValue.FromObject(1)));
        Assert.Throws<FormatException>(() => ConcatValues.Join(text, NodeValue.FromBytes(new byte[] { 1 })));
        Assert.Throws<FormatException>(() => ConcatValues.Join([.. text, .. text], NodeValue.FromObject(new string('x', 400000))));
        Assert.Throws<FormatException>(() => ConcatValues.Join([], NodeValue.FromObject(",")));
    }

    /// <summary>区切りを指定したPath連結はStringで、既定はPath結合のままです。</summary>
    [Theory]
    [InlineData("", "path", "group/zone")]
    [InlineData("_", "string", "group_zone")]
    public void PathInferenceAgreesWithTheJoinedValue(string separator, string type, string expected)
    {
        NodeDefinition node = new("concat", "data.concat", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["input1"] = PathValues.Create("group"), ["input2"] = PathValues.Create("zone"), ["separator"] = NodeValue.FromObject(separator),
        });
        WorkflowDefinition document = new("test", "test", [node], []);
        Assert.Equal(type, new WorkflowTypeInference(document, [new ConcatNode().Descriptor]).PortType(node.Id, "value", true)!.Type);
        NodeValue value = new WorkflowSettingValues(document).Output(node.Id)!;
        Assert.Equal(expected, value.GetString());
        Assert.Equal(type, PathTypeMetadata.TypeOf(value)!.Type);
    }

    /// <summary>実行時の区切りがまだ届かない段階で、空文字連結を確定表示しません。</summary>
    /// <returns>動的配線と定数配線の実行一致です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WiredSeparatorIsUsedByTheEngineAndPreviewAsync(bool dynamic)
    {
        WorkflowFixture fixture = new(new ConcatNode(), new ConstantValueNode(), new PassNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeDefinition source = new("sep", "value.constant", JsonSerializer.SerializeToElement(new { text = ":", type = "string" }));
        if (dynamic)
        {
            source = new("sep", "control.pass", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(":") });
        }
        NodeDefinition node = new("concat", "data.concat", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["input1"] = NodeValue.FromObject("group"), ["input2"] = NodeValue.FromObject("zone"),
        });
        WorkflowDefinition document = new("test", "test", [source, node], [new("sep", "value", "concat", "separator")]);
        CompilationResult compiled = fixture.Compiler.Compile(document);
        Assert.Empty(compiled.Issues);
        NodeValue? preview = new WorkflowSettingValues(document).Output(node.Id);
        Assert.Equal(!dynamic, preview is not null);
        if (preview is not null)
        {
            Assert.Equal("group:zone", preview.GetString());
        }
        ExecutionReport result = await fixture.Engine.ExecuteAsync(compiled.Workflow!).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("group:zone", Assert.Single(result.Nodes.Single(item => string.Equals(item.NodeId, node.Id, StringComparison.Ordinal)).Targets).Result.Outputs["value"].GetString());
    }
}
