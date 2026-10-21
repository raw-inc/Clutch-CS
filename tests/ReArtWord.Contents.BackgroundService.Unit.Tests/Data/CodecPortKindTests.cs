using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>成功時の実codec出力とカタログの値型を一致させます。</summary>
public sealed class CodecPortKindTests
{
    /// <summary>テキストとUTF8のどちらを解析してもJSON値を返します。</summary>
    /// <returns>入力の受理範囲と出力型の確認です。</returns>
    [Theory]
    [InlineData("json", "{\"n\":42}")]
    [InlineData("yaml", "n: 42")]
    [InlineData("toml", "n = 42")]
    [InlineData("csv", "one,two\r\n")]
    [InlineData("text", "hello")]
    public async Task TextParsersDeclareJsonForBothAcceptedInputsAsync(string format, string text)
    {
        SerializationNode node = new(format, false);
        Assert.Equal(EPortValueKind.Any, node.Descriptor.Inputs.Single(port => string.Equals(port.Name, "value", StringComparison.Ordinal)).ValueKind);
        Assert.Equal(EPortValueKind.Json, node.Descriptor.Outputs.Single().ValueKind);
        NodeValue[] inputs = [NodeValue.FromObject(text), NodeValue.FromBytes(Encoding.UTF8.GetBytes(text))];
        foreach (NodeValue input in inputs)
        {
            NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(value: input)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(EValueKind.Json, result.Outputs["value"].Kind);
        }
    }

    /// <summary>文字列を出力するserializeもBinaryではなくJSON stringです。</summary>
    /// <returns>宣言型と実出力の確認です。</returns>
    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("toml")]
    [InlineData("csv")]
    [InlineData("text")]
    public async Task TextSerializersDeclareJsonStringsAsync(string format)
    {
        SerializationNode node = new(format, true);
        string[][] rows = [["one", "two"]];
        NodeValue input = format switch
        {
            "csv" => NodeValue.FromObject(rows),
            "text" => NodeValue.FromObject("hello"),
            _ => NodeValue.FromObject(new { n = 42 }),
        };
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(value: input)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(EPortValueKind.Json, node.Descriptor.Outputs.Single().ValueKind);
        Assert.Equal(EValueKind.Json, result.Outputs["value"].Kind);
        Assert.NotNull(result.Outputs["value"].GetString());
    }

    /// <summary>バイナリ parseの未指定pass-throughを維持し、設定依存の二種類をAnyで宣言します。</summary>
    /// <returns>暗黙に既存動作を変えない型確認です。</returns>
    [Fact]
    public async Task BinaryParsingRemainsAnyWhileEncodingIsAlwaysBinaryAsync()
    {
        SerializationNode parse = new("binary", false);
        SerializationNode serialize = new("binary", true);
        NodeValue bytes = NodeValue.FromBytes(new byte[] { 42 });
        string settings = """{"fields":[{"name":"n","type":"uint8","offset":0}]}""";
        NodeResult raw = await parse.ExecuteAsync(NodeTestContext.Create(value: bytes)).ConfigureAwait(true);
        NodeResult structured = await parse.ExecuteAsync(NodeTestContext.Create(settings, bytes)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, raw.Outcome);
        Assert.Equal(EOutcome.Succeeded, structured.Outcome);
        Assert.Equal(EValueKind.Binary, raw.Outputs["value"].Kind);
        Assert.Equal(EValueKind.Json, structured.Outputs["value"].Kind);
        Assert.Equal(42, structured.Outputs["value"].Json.GetProperty("n").GetInt32());
        Assert.Equal(EPortValueKind.Any, parse.Descriptor.Outputs.Single().ValueKind);
        NodeResult copied = await serialize.ExecuteAsync(NodeTestContext.Create(value: bytes)).ConfigureAwait(true);
        NodeResult encoded = await serialize.ExecuteAsync(NodeTestContext.Create(settings, structured.Outputs["value"])).ConfigureAwait(true);
        foreach (NodeResult result in new[] { copied, encoded })
        {
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal(EValueKind.Binary, result.Outputs["value"].Kind);
            Assert.Equal(bytes.Binary.ToArray(), result.Outputs["value"].Binary.ToArray());
        }
        Assert.Equal(EPortValueKind.Binary, serialize.Descriptor.Outputs.Single().ValueKind);
        Assert.Equal(EPortValueKind.Any, new ExtractNode().Descriptor.Outputs[0].ValueKind);
        Assert.Equal(EPortValueKind.Any, new TransformNode().Descriptor.Outputs[0].ValueKind);
    }
}
