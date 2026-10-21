using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>結合・分類・展開の実データと、部分成功しない容量境界を確認します。</summary>
public sealed class CollectionNodeTests
{
    /// <summary>重複キーは全一致を返し、外部結合の未一致側は明示nullにします。</summary>
    /// <returns>入力順と外部結合の確認です。</returns>
    [Fact]
    public async Task FullJoinKeepsDuplicatesAndUnmatchedRowsAsync()
    {
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal)
        {
            ["left"] = Value("""[{"id":1},{"id":2},{"id":null}]"""),
            ["right"] = Value("""[{"id":1,"v":"a"},{"id":1,"v":"b"},{"id":3}]"""),
        };
        NodeResult result = await new CollectionNode("join").ExecuteAsync(NodeTestContext.Create("""{"leftPath":"/id","rightPath":"/id","kind":"full"}""", extra: inputs)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        JsonElement rows = result.Outputs["value"].Json;
        Assert.Equal(5, rows.GetArrayLength());
        Assert.Equal("a", rows[0].GetProperty("right").GetProperty("v").GetString());
        Assert.Equal("b", rows[1].GetProperty("right").GetProperty("v").GetString());
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("right").ValueKind);
        Assert.Equal(3, rows[4].GetProperty("right").GetProperty("id").GetInt32());
    }

    /// <summary>キー初出順、nullと数値の等価性を維持して組を作成します。</summary>
    /// <returns>グループ構成の確認です。</returns>
    [Fact]
    public async Task GroupKeepsFirstOccurrenceOrderAndExactValuesAsync()
    {
        NodeResult result = await new CollectionNode("group").ExecuteAsync(NodeTestContext.Create(value: Value("""["x",null,"x",1,1.0]"""))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        JsonElement rows = result.Outputs["value"].Json;
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("x", rows[0].GetProperty("key").GetString());
        Assert.Equal(2, rows[0].GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("key").ValueKind);
        Assert.Equal(2, rows[2].GetProperty("items").GetArrayLength());
    }

    /// <summary>配列だけを指定深さまで展開し、nullは削除しません。</summary>
    /// <returns>深さ境界と順序の確認です。</returns>
    [Theory]
    [InlineData(1, "[1,2,[3],null]")]
    [InlineData(2, "[1,2,3,null]")]
    public async Task FlattenRespectsDepthAsync(int depth, string expected)
    {
        NodeResult result = await new CollectionNode("flatten").ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { depth }), Value("[1,[2,[3]],null,[]]"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(JsonElement.DeepEquals(Value(expected).Json, result.Outputs["value"].Json));
    }

    /// <summary>展開対象以外の親項目を保持し、空集合の扱いは設定に従います。</summary>
    /// <returns>展開した実データの確認です。</returns>
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public async Task ExpandPreservesParentFieldsAsync(bool keepEmpty, int count)
    {
        NodeResult result = await new CollectionNode("expand").ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { field = "items", keepEmpty }),
            Value("""[{"parent":"x","items":[1,2]},{"parent":"y","items":[]}]"""))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(count, result.Outputs["value"].Json.GetArrayLength());
        Assert.Equal("x", result.Outputs["value"].Json[1].GetProperty("parent").GetString());
        Assert.Equal(2, result.Outputs["value"].Json[1].GetProperty("items").GetInt32());
    }

    /// <summary>数値比較不能を不一致集合へ混ぜません。</summary>
    /// <returns>三つの出力集合の確認です。</returns>
    [Fact]
    public async Task SplitRetainsIndeterminateItemsAsync()
    {
        NodeResult result = await new CollectionNode("split").ExecuteAsync(NodeTestContext.Create("""{"comparison":"less"}""", Value("""[1,"a",2,null]"""),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["expected"] = Value("2") })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(JsonElement.DeepEquals(Value("[1]").Json, result.Outputs["matched"].Json));
        Assert.True(JsonElement.DeepEquals(Value("[2]").Json, result.Outputs["rejected"].Json));
        Assert.True(JsonElement.DeepEquals(Value("""["a",null]""").Json, result.Outputs["indeterminate"].Json));
    }

    /// <summary>結合の増幅が上限を超えた場合は部分配列を成功として返しません。</summary>
    /// <returns>容量・仕事量上限の確認です。</returns>
    [Theory]
    [InlineData("{\"maxItems\":2}")]
    [InlineData("{\"maxWork\":1}")]
    [InlineData("{\"maxBytes\":10}")]
    public async Task JoinBoundsDoNotReturnPartialSuccessAsync(string settings)
    {
        NodeResult result = await new CollectionNode("join").ExecuteAsync(NodeTestContext.Create(settings, extra:
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["left"] = Value("[1,1]"), ["right"] = Value("[1,1]") })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>定義と同じJSON値を用意します。</summary>
    /// <returns>所有済みの試験入力です。</returns>
    private static NodeValue Value(string json) => NodeValue.FromJson(NodeSettings.Schema(json));
}
