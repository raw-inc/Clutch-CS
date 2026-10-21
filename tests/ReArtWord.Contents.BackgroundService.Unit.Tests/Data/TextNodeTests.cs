using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>文字列処理の型・C#のキャプチャ・不一致と失敗の境界を確認します。</summary>
public sealed class TextNodeTests
{
    private static readonly string[] expectedNames = ["alice", "bob"];
    private static readonly string[] expectedAges = ["12", "34"];

    /// <summary>再利用したパターンでも大文字小文字・改行方針を混ぜず、上限を跨いだ後も正しく再構築します。</summary>
    /// <returns>同じノード実体への異なる入力と設定の結果です。</returns>
    [Fact]
    public async Task ReusedPatternsKeepMatchingOptionsIndependent()
    {
        RegexNode node = new(true);
        for (int index = 0; index < 4; index++)
        {
            bool enabled = index % 2 == 0;
            NodeResult casing = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { ignoreCase = enabled }), extra: Inputs("ABC", "abc"))).ConfigureAwait(true);
            NodeResult multiline = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { multiline = enabled }), extra: Inputs("a\nb", "^b"))).ConfigureAwait(true);
            NodeResult singleline = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { singleline = enabled }), extra: Inputs("a\nb", "a.b"))).ConfigureAwait(true);
            Assert.Equal(enabled, casing.Outputs["value"].Json.GetBoolean());
            Assert.Equal(enabled, multiline.Outputs["value"].Json.GetBoolean());
            Assert.Equal(enabled, singleline.Outputs["value"].Json.GetBoolean());
        }
        for (int index = 0; index < 80; index++)
        {
            string value = "value" + index;
            NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(extra: Inputs(value, "^" + value + "$"))).ConfigureAwait(true);
            Assert.True(result.Outputs["value"].Json.GetBoolean());
        }
        NodeResult restored = await node.ExecuteAsync(NodeTestContext.Create(extra: Inputs("ABC", "abc"))).ConfigureAwait(true);
        Assert.False(restored.Outputs["value"].Json.GetBoolean());
    }

    /// <summary>名前付き出力が各一致のキャプチャを順序通り保持します。</summary>
    /// <returns>全件・最初のみの確認です。</returns>
    [Theory]
    [InlineData("all", 2)]
    [InlineData("first", 1)]
    public async Task NamedGroupsRespectMatchMode(string mode, int count)
    {
        NodeResult result = await RegexAsync("alice-12 bob-34", @"(?<name>\w+)-(?<age>\d+)", mode);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(count, result.Outputs["count"].Json.GetInt32());
        JsonElement values = result.Outputs["value"].Json;
        if (count == 2)
        {
            Assert.Equal(expectedNames, values.GetProperty("name").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(expectedAges, values.GetProperty("age").EnumerateArray().Select(item => item.GetString()));
        }
        else
        {
            Assert.Equal("alice", values.GetProperty("name").GetString());
            Assert.Equal("12", values.GetProperty("age").GetString());
        }
    }

    /// <summary>親の繰り返し・重複名・inline指定でも複数キャプチャを落としません。</summary>
    /// <returns>可変個数のグループと型の一致です。</returns>
    [Theory]
    [InlineData(@"(?<digit>\d)+", "123", "digit", 3)]
    [InlineData(@"(?:(?<digit>\d),?){3}", "1,2,3", "digit", 3)]
    [InlineData(@"(?<digit>\d)(?<digit>\d)", "12", "digit", 2)]
    [InlineData(@"(?n)(?<digit>\d)+", "123", "digit", 3)]
    [InlineData(@"(?<word>[a-z]+)", "abc", "word", 0)]
    [InlineData(@"(a)(?<word>b)", "ab", "1", 0)]
    public async Task CaptureShapeMatchesRuntime(string pattern, string input, string group, int captures)
    {
        NodeResult result = await RegexAsync(input, pattern, "first");
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        ValueTypeDefinition field = new RegexTextPattern(pattern).OutputType(true).Properties![group];
        JsonElement value = result.Outputs["value"].Json.GetProperty(group);
        if (0 < captures)
        {
            Assert.Equal("array", field.Type);
            Assert.Equal(captures, value.GetArrayLength());
        }
        else
        {
            Assert.Equal("string", field.Type);
            Assert.Equal(JsonValueKind.String, value.ValueKind);
        }
    }

    /// <summary>不一致・空一致・不正パターンを区別します。</summary>
    /// <returns>静かな切捨てや正常な不一致への置換がないことの確認です。</returns>
    [Fact]
    public async Task MissingMatchesAndInvalidPatternsAreDistinct()
    {
        NodeResult none = await RegexAsync("abc", @"(?<n>\d+)", "first");
        Assert.Equal(EOutcome.Succeeded, none.Outcome);
        Assert.Equal(JsonValueKind.Null, none.Outputs["value"].Json.GetProperty("n").ValueKind);
        NodeResult empty = await RegexAsync("ab", string.Empty, "all");
        Assert.Equal(3, empty.Outputs["count"].Json.GetInt32());
        Assert.NotEqual(EOutcome.Succeeded, (await RegexAsync("abc", "(", "all")).Outcome);
        Assert.Equal("text.regex.matches.limit", (await RegexAsync(new string('a', 5000), string.Empty, "all")).ReasonCode);
        NodeResult timeout = await new RegexNode().ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { timeoutMilliseconds = 1 }),
            extra: Inputs(new string('a', 100000) + "!", "^(a+)+$")));
        Assert.Equal("text.regex.timeout", timeout.ReasonCode);
    }

    /// <summary>Matchは正規表現の部分一致を判定し、照合失敗をFalseと偽りません。</summary>
    /// <returns>boolの成功値です。</returns>
    [Theory]
    [InlineData("abc123", @"\d+", true)]
    [InlineData("abc", @"\d+", false)]
    [InlineData("ABC", "abc", true)]
    public async Task MatchReturnsBoolean(string input, string pattern, bool expected)
    {
        NodeResult result = await new RegexNode(true).ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { ignoreCase = true }), extra: Inputs(input, pattern)));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].Json.GetBoolean());
    }

    /// <summary>UTF-16位置、空範囲、末尾までと範囲エラーを確認します。</summary>
    /// <returns>C#と同じ0始まりの位置での取得です。</returns>
    [Theory]
    [InlineData("abcde", 1, 2, "bc")]
    [InlineData("abcde", 2, null, "cde")]
    [InlineData("abc", 3, 0, "")]
    [InlineData("a😀b", 1, 2, "😀")]
    public async Task SubstringUsesExplicitRange(string text, int start, int? length, string expected)
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(text), ["startIndex"] = NodeValue.FromObject(start) };
        if (length.HasValue)
        {
            inputs["length"] = NodeValue.FromObject(length.Value);
        }
        NodeResult result = await new SubstringNode().ExecuteAsync(NodeTestContext.Create(extra: inputs));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(expected, result.Outputs["value"].GetString());
        inputs["startIndex"] = NodeValue.FromObject(-1);
        Assert.Equal(EOutcome.Failed, (await new SubstringNode().ExecuteAsync(NodeTestContext.Create(extra: inputs))).Outcome);
    }

    /// <summary>新規処理は同期値演算であり、汎用の完了信号を追加しません。</summary>
    [Fact]
    public void CatalogHasNoAsynchronousCompletionPorts()
    {
        NodeRegistry registry = new([new RegexNode(), new RegexNode(true), new SubstringNode()]);
        Assert.All(registry.Descriptors, descriptor => Assert.DoesNotContain(descriptor.Outputs, port => string.Equals(port.Name, "$completed", StringComparison.Ordinal)));
    }

    /// <summary>同じ入力形式で通常のノード実行を呼び出します。</summary>
    /// <returns>抽出ノードの結果です。</returns>
    private static ValueTask<NodeResult> RegexAsync(string text, string pattern, string mode)
        => new RegexNode().ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { mode }), extra: Inputs(text, pattern)));

    /// <summary>テキストとパターンを独立した入力値へ変換します。</summary>
    /// <returns>通常入力の辞書です。</returns>
    private static Dictionary<string, NodeValue> Inputs(string text, string pattern)
        => new(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(text), ["pattern"] = NodeValue.FromObject(pattern) };
}
