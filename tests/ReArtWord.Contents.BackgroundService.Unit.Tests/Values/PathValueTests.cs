using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Values;

/// <summary>Pathを文字列として運び、区切り・型情報・不正入力の境界を確認します。</summary>
public sealed class PathValueTests
{
    /// <summary>ルートを維持し、相対要素やUnicode名も区切りだけを正規化します。</summary>
    [Theory]
    [InlineData(@"C:\content\日本語.wav", "C:/content/日本語.wav")]
    [InlineData(@"\\server\share\folder\", @"\\server\share\folder\")]
    [InlineData(@"\\con\share\file", @"\\con\share\file")]
    [InlineData(@"folder\sub//file", "folder/sub/file")]
    [InlineData("/var/data/file", "/var/data/file")]
    [InlineData("../folder/./file", "../folder/./file")]
    [InlineData("/", "/")]
    public void PathsStayStringsAndNormalizeSeparators(string input, string expected)
    {
        NodeValue value = Constant(input);
        Assert.Equal(EValueKind.Json, value.Kind);
        Assert.Equal(JsonValueKind.String, value.Json.ValueKind);
        Assert.Equal(expected, value.GetString());
        Assert.Equal("path", value.DataType!.Type);
        Assert.Equal(expected, PathValues.Normalize(expected));
    }

    /// <summary>名前として解釈できない文字、予約名、曖昧ルートを修正せず拒否します。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("bad\0name")]
    [InlineData("bad\nname")]
    [InlineData("file?.txt")]
    [InlineData("*.wav")]
    [InlineData("file|name")]
    [InlineData("file:name")]
    [InlineData("CON.txt")]
    [InlineData("COM¹.txt")]
    [InlineData("LPT9")]
    [InlineData("folder./file")]
    [InlineData("folder /file")]
    [InlineData("C:relative")]
    [InlineData("//server")]
    [InlineData("//?/C:/file")]
    [InlineData("https://example.com/file")]
    public void InvalidPathsAreNotSilentlySanitized(string input)
    {
        Assert.Throws<FormatException>(() => PathValues.Create(input));
        NodeResult result = new ConstantValueNode().Evaluate(Definition("value.constant", new { text = input, type = "path" }), Empty());
        Assert.NotEqual(EOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Outputs);
    }

    /// <summary>Pathだけを結合すると区切りを補い、文字列混在は既存の文字列連結です。</summary>
    [Fact]
    public void ConcatPreservesPathSemanticsWithoutChangingStringConcatenation()
    {
        NodeValue root = Constant(@"C:\Clutch-CS\content\"), child = Constant(@"audio\clip.wav");
        NodeValue joined = ConcatValues.Join([root, child]);
        Assert.Equal("C:/Clutch-CS/content/audio/clip.wav", joined.GetString());
        Assert.Equal("path", joined.DataType!.Type);
        Assert.Equal("path", ConcatValues.ResultType([new("path"), new("path")])!.Type);
        NodeValue mixed = ConcatValues.Join([Constant("prefix"), NodeValue.FromObject("suffix")]);
        Assert.Equal("prefixsuffix", mixed.GetString());
        Assert.Null(mixed.DataType);
        Assert.Equal("string", ConcatValues.ResultType([new("path"), new("string")])!.Type);
        Assert.Equal("string", ConcatValues.ResultType([new("path"), null])!.Type);
        Assert.Throws<FormatException>(() => ConcatValues.Join([root, Constant("D:/different")]));
        Assert.Throws<FormatException>(() => ConcatValues.Join([root, Constant("/absolute")]));
    }

    /// <summary>別OSのルートを捏造せず、明示された実行先の区切りへ変換します。</summary>
    [Fact]
    public void NativeConversionDoesNotGuessDriveOrShareMappings()
    {
        Assert.Equal(@"folder\file", PathValues.ToNative("folder/file", true));
        Assert.Equal("folder/file", PathValues.ToNative(@"folder\file", false));
        Assert.Equal(@"C:\folder\file", PathValues.ToNative("C:/folder/file", true));
        Assert.Equal(@"\\server\share\file", PathValues.ToNative("//server/share/file", true));
        Assert.Throws<FormatException>(() => PathValues.ToNative("C:/folder/file", false));
        Assert.Throws<FormatException>(() => PathValues.ToNative("//server/share/file", false));
        Assert.Throws<FormatException>(() => PathValues.ToNative("/var/data", true));
    }

    /// <summary>JSON本文を包み直さず、配列・Object・Pointer・通信複製でPathの型を保ちます。</summary>
    [Fact]
    public void NestedPathsSurviveConstructionPointerAndWireRoundTrip()
    {
        KeyValueNode keyed = new();
        NodeValue obj = keyed.Evaluate(Definition(keyed.Descriptor.Type, new { key = "file" }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = Constant(@"folder\clip.wav") }).Outputs["value"];
        ArrayValueNode array = new();
        NodeValue value = array.Evaluate(Definition(array.Descriptor.Type, new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["item"] = obj }).Outputs["value"];
        string json = JsonSerializer.Serialize(value, ContractJson.Options);
        NodeValue restored = JsonSerializer.Deserialize<NodeValue>(json, ContractJson.Options)!.Copy();
        NodeValue selected = JsonPointer.Select(restored, "/0/file");
        Assert.Equal("folder/clip.wav", selected.GetString());
        Assert.Equal("path", selected.DataType!.Type);
        Assert.Equal(JsonValueKind.String, restored.Json[0].GetProperty("file").ValueKind);
        Assert.Equal("path", PathTypeMetadata.TypeOf(restored)!.ElementType!.Properties!["file"].Type);
        NodeValue replaced = keyed.Evaluate(Definition(keyed.Descriptor.Type, new { key = "file", duplicate = "replace" }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["base"] = obj, ["value"] = NodeValue.FromObject("plain") }).Outputs["value"];
        Assert.Null(JsonPointer.Select(replaced, "/file").DataType);
        Assert.Equal("string", replaced.DataType!.ValueType!.Type);
    }

    /// <summary>JSON SchemaのPath宣言で文字列を検証し、空配列の型も結合後に維持します。</summary>
    [Fact]
    public void SchemaAndEmptyArraysRetainTheirPathType()
    {
        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","required":["file"],"properties":{"file":{"type":"string","format":"path"}}}""");
        ValueTypeDefinition type = JsonSchemaTypes.Read(schema);
        NodeValue value = PathTypeMetadata.Apply(NodeValue.FromObject(new { file = @"a\b" }), type);
        Assert.Equal("a/b", value.Json.GetProperty("file").GetString());
        Assert.Equal("path", JsonPointer.Select(value, "/file").DataType!.Type);
        Assert.Throws<FormatException>(() => PathTypeMetadata.Apply(NodeValue.FromObject(new { file = "bad*file" }), type));
        Assert.Throws<FormatException>(() => PathTypeMetadata.Apply(NodeValue.FromObject("not an object"), type));
        Assert.Throws<FormatException>(() => PathTypeMetadata.Apply(NodeValue.FromObject(new { }), type));
        Assert.Throws<FormatException>(() => PathValues.Create(new string((char)0xd800, 1)));
        NodeValue empty = PathTypeMetadata.Apply(NodeValue.FromObject(Array.Empty<string>()), new("array", new("path")));
        NodeValue combined = ConcatValues.Join([empty, empty]);
        Assert.Equal(0, combined.Json.GetArrayLength());
        Assert.Equal("path", combined.DataType!.ElementType!.Type);
        Assert.True(ValueTypeInference.CanAssign(new("string"), new("path")));
        Assert.True(ValueTypeInference.CanAssign(new("path"), new("string")));
        Assert.False(ValueTypeInference.CanAssign(new("path"), new("integer")));
    }

    /// <summary>明示変換でPathとStringを相互に扱い、数値はPathへ暗黙変換しません。</summary>
    /// <returns>実変換ノードの結果です。</returns>
    [Fact]
    public async Task ConversionPromotesAndRemovesPathSemantics()
    {
        ConvertNode convert = new();
        NodeResult path = await convert.ExecuteAsync(NodeTestContext.Create("""{"targetType":"path"}""", NodeValue.FromObject(@"a\b"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, path.Outcome);
        Assert.Equal("path", path.Outputs["value"].DataType!.Type);
        NodeResult text = await convert.ExecuteAsync(NodeTestContext.Create("""{"targetType":"string"}""", path.Outputs["value"])).ConfigureAwait(true);
        Assert.Equal("a/b", text.Outputs["value"].GetString());
        Assert.Null(text.Outputs["value"].DataType);
        NodeResult invalid = await convert.ExecuteAsync(NodeTestContext.Create("""{"targetType":"path"}""", NodeValue.FromObject(123))).ConfigureAwait(true);
        Assert.NotEqual(EOutcome.Succeeded, invalid.Outcome);
    }

    /// <summary>実定数ノードでPathを生成します。</summary>
    /// <returns>正常なPath出力です。</returns>
    private static NodeValue Constant(string path)
    {
        NodeResult result = new ConstantValueNode().Evaluate(Definition("value.constant", new { text = path, type = "path" }), Empty());
        Assert.True(result.Outcome == EOutcome.Succeeded, result.ReasonCode);
        return result.Outputs["value"];
    }

    /// <summary>設定だけを持つ純粋値ノードです。</summary>
    /// <returns>試験用定義です。</returns>
    private static NodeDefinition Definition(string type, object settings) => new("node", type, JsonSerializer.SerializeToElement(settings));

    /// <summary>空の通常入力です。</summary>
    /// <returns>比較方法を固定した辞書です。</returns>
    private static Dictionary<string, NodeValue> Empty() => new(StringComparer.Ordinal);
}
