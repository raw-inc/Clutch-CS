using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>標準形式の構造と欠損境界を検証します。</summary>
public sealed class DataNodeTests
{
    /// <summary>入れ子・配列・引用内改行を標準解析器で読み取ります。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("json", "{\"items\":[null,9007199254740993]}", "/items/1", "9007199254740993")]
    [InlineData("yaml", "items:\n  - null\n  - 42\n", "/items/1", "42")]
    [InlineData("toml", "[section]\nvalues = [1, 2]\n", "/section/values/1", "2")]
    [InlineData("csv", "\"a,b\",\"line1\nline2\"\r\n", "/0/1", "line1\nline2")]
    [InlineData("text", "plain\ntext", "", "plain\ntext")]
    public async Task ParsePreservesStructuredData(string format, string text, string path, string expected)
    {
        SerializationNode parser = new(format, false);
        NodeResult parsed = await parser.ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(text)));
        Assert.Equal(EOutcome.Succeeded, parsed.Outcome);
        ExtractNode extractor = new();
        string settings = JsonSerializer.Serialize(new { mode = "pointer", path = path });
        NodeResult result = await extractor.ExecuteAsync(NodeTestContext.Create(settings, parsed.Outputs["value"]));
        Assert.Equal(expected, result.Outputs["value"].Json.ToString());
    }

    /// <summary>正常nullとnullを含む配列のCountを混同しません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("null", "", 0)]
    [InlineData("[]", "", 0)]
    [InlineData("[null,null]", "", 2)]
    [InlineData("{}", "/absent", 0)]
    [InlineData("42", "", 1)]
    public async Task ExtractCountUsesResultElements(string json, string path, int count)
    {
        ExtractNode node = new();
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { mode = "pointer", path = path }), NodeValue.FromJson(NodeSettings.Schema(json))));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(count, result.Outputs["count"].Json.GetInt32());
    }

    /// <summary>同じ明示レイアウトで精度とバイト順を往復保持します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task BinaryLayoutRoundTripsBothByteOrders()
    {
        string settings = """{"fields":[{"name":"id","type":"uint32","offset":0,"endian":"big"},{"name":"signed","type":"int16","offset":4,"endian":"little"}]}""";
        NodeValue input = NodeValue.FromBytes(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0xFF });
        NodeResult decoded = await new SerializationNode("binary", false).ExecuteAsync(NodeTestContext.Create(settings, input));
        Assert.Equal(uint.MaxValue, decoded.Outputs["value"].Json.GetProperty("id").GetUInt32());
        Assert.Equal(-2, decoded.Outputs["value"].Json.GetProperty("signed").GetInt32());
        NodeResult encoded = await new SerializationNode("binary", true).ExecuteAsync(NodeTestContext.Create(settings, decoded.Outputs["value"]));
        Assert.Equal(input.Binary.ToArray(), encoded.Outputs["value"].Binary.ToArray());
    }

    /// <summary>壊れた構文を正常nullへ丸めません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("json", "{broken")]
    [InlineData("yaml", "items: [1,\n")]
    [InlineData("yaml", "&loop [*loop]")]
    [InlineData("toml", "key = [")]
    public async Task InvalidInputFails(string format, string text)
    {
        NodeResult result = await new SerializationNode(format, false).ExecuteAsync(NodeTestContext.Create(value: NodeValue.FromObject(text)));
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>標準形式への書き出し後に同じ値を読み戻せます。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("toml")]
    public async Task StructuredFormatsRoundTrip(string format)
    {
        NodeValue input = NodeValue.FromJson(NodeSettings.Schema("""{"name":"hello","nested":{"number":42},"list":[1,2]}"""));
        NodeResult serialized = await new SerializationNode(format, true).ExecuteAsync(NodeTestContext.Create(value: input));
        Assert.Equal(EOutcome.Succeeded, serialized.Outcome);
        NodeResult parsed = await new SerializationNode(format, false).ExecuteAsync(NodeTestContext.Create(value: serialized.Outputs["value"]));
        Assert.Equal(EOutcome.Succeeded, parsed.Outcome);
        Assert.True(JsonElement.DeepEquals(input.Json, parsed.Outputs["value"].Json));
    }

    /// <summary>固定値と元入力パスから構造を作り元値を変えません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task TransformBuildsStructureWithoutMutatingInput()
    {
        NodeValue input = NodeValue.FromJson(NodeSettings.Schema("""{"id":3,"name":"Alice"}"""));
        TransformNode node = new();
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create("""{"operation":"create","fields":{"user_id":{"$path":"/id"},"active":true}}""", input));
        Assert.Equal(3, result.Outputs["value"].Json.GetProperty("user_id").GetInt32());
        Assert.True(result.Outputs["value"].Json.GetProperty("active").GetBoolean());
        Assert.Equal("Alice", input.Json.GetProperty("name").GetString());
    }

    /// <summary>設定名の誤記は実行前に拒否します。</summary>
    [Fact]
    public void UnknownSettingDoesNotSilentlyUseDefault()
    {
        ExtractNode node = new();
        Assert.NotEmpty(node.Validate(new NodeDefinition("n", node.Descriptor.Type, NodeSettings.Schema("""{"mode":"line","row":0,"colum":4}"""))));
    }

    /// <summary>バイトフィールドの長さ漏れと重複範囲を開始前に拒否します。</summary>
    [Theory]
    [InlineData("{\"fields\":[{\"name\":\"data\",\"type\":\"bytes\",\"offset\":0}]}")]
    [InlineData("{\"fields\":[{\"name\":\"a\",\"type\":\"uint32\",\"offset\":0},{\"name\":\"b\",\"type\":\"uint32\",\"offset\":2}]}")]
    public void BinaryLayoutInvalidDefinitionIsRejected(string settings)
    {
        SerializationNode node = new("binary", false);
        Assert.NotEmpty(node.Validate(new NodeDefinition("n", node.Descriptor.Type, NodeSettings.Schema(settings))));
    }

    /// <summary>CSV書き出しで引用符とセル内改行を失いません。</summary>
    /// <returns>往復検証の完了です。</returns>
    [Fact]
    public async Task CsvSerializationPreservesQuotedCells()
    {
        string[] row = ["a,b", "line1\nline2", "a\"b"];
        NodeValue input = NodeValue.FromObject(new[] { row });
        NodeResult serialized = await new SerializationNode("csv", true).ExecuteAsync(NodeTestContext.Create(value: input));
        NodeResult parsed = await new SerializationNode("csv", false).ExecuteAsync(NodeTestContext.Create(value: serialized.Outputs["value"]));
        Assert.True(JsonElement.DeepEquals(input.Json, parsed.Outputs["value"].Json));
    }

    /// <summary>セル指定と正規表現の一致なしを正常nullとして扱います。</summary>
    /// <returns>欠損境界の検証の完了。</returns>
    [Fact]
    public async Task ExtractionMissingIsNormalNull()
    {
        ExtractNode node = new();
        NodeResult cell = await node.ExecuteAsync(NodeTestContext.Create("""{"mode":"cell","row":1,"column":4}""", NodeValue.FromJson(NodeSettings.Schema("""[["a"]]"""))));
        NodeResult match = await node.ExecuteAsync(NodeTestContext.Create("""{"mode":"regex","pattern":"z+"}""", NodeValue.FromObject("abc")));
        Assert.Equal(EOutcome.Succeeded, cell.Outcome);
        Assert.Equal(JsonValueKind.Null, cell.Outputs["value"].Json.ValueKind);
        Assert.Equal(EOutcome.Succeeded, match.Outcome);
        Assert.Equal(JsonValueKind.Null, match.Outputs["value"].Json.ValueKind);
    }
}
