using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Data;

/// <summary>表示用の推測を、変換・抽出ノードの実際の出力と照合します。</summary>
public sealed class OutputTypeInferenceTests
{
    /// <summary>解析器が返すスカラー・配列・オブジェクトを保存形式だけで分類しません。</summary>
    /// <returns>実ノードの実行と推測の照合です。</returns>
    [Theory]
    [InlineData("json", "42", "integer")]
    [InlineData("json", "true", "boolean")]
    [InlineData("json", "\"text\"", "string")]
    [InlineData("json", "[1,2]", "array")]
    [InlineData("json", "{\"id\":1}", "object")]
    [InlineData("csv", "a,b\n1,2", "array")]
    [InlineData("toml", "key=1", "object")]
    [InlineData("text", "plain text", "string")]
    public async Task ParserTypesMatchActualValues(string format, string text, string expected)
    {
        await VerifyAsync(new SerializationNode(format, false), "{}", NodeValue.FromObject(text), expected).ConfigureAwait(true);
    }

    /// <summary>JSONを生成するノードの出力は、JSONオブジェクトではなく文字列です。</summary>
    /// <returns>実際の文字列出力の確認です。</returns>
    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("toml")]
    public async Task SerializersProduceText(string format)
    {
        await VerifyAsync(new SerializationNode(format, true), "{}", Value("{\"id\":1}"), "string").ConfigureAwait(true);
    }

    /// <summary>バイト列の解析は、レイアウト指定の有無で出力型が変わります。</summary>
    /// <returns>実際のバイト列とオブジェクトの照合です。</returns>
    [Theory]
    [InlineData("{}", "binary")]
    [InlineData("{\"fields\":[{\"name\":\"id\",\"type\":\"uint32\",\"offset\":0,\"endian\":\"big\"}]}", "object")]
    public async Task BinaryLayoutsChangeTheOutputType(string settings, string expected)
    {
        await VerifyAsync(new SerializationNode("binary", false), settings, NodeValue.FromBytes(new byte[] { 0, 0, 0, 42 }), expected).ConfigureAwait(true);
    }

    /// <summary>抽出では値がない場合を許容しつつ、分かる値の種類を表示します。</summary>
    /// <returns>実値と欠損時のnullを照合します。</returns>
    [Theory]
    [InlineData("{\"mode\":\"pointer\",\"path\":\"/items/0\"}", "{\"items\":[42]}", "integer")]
    [InlineData("{\"mode\":\"pointer\",\"path\":\"/items/20\"}", "{\"items\":[42]}", "integer")]
    [InlineData("{\"mode\":\"row\",\"row\":0}", "[true,false]", "boolean")]
    [InlineData("{\"mode\":\"row\",\"row\":20}", "[true,false]", "boolean")]
    [InlineData("{\"mode\":\"cell\",\"row\":0,\"column\":1}", "[[1,2],[3,4]]", "integer")]
    [InlineData("{\"mode\":\"line\",\"row\":0}", "\"abc\"", "string")]
    [InlineData("{\"mode\":\"line\",\"row\":9}", "\"abc\"", "string")]
    [InlineData("{\"mode\":\"regex\",\"pattern\":\"a+\"}", "\"abc\"", "array")]
    [InlineData("{\"mode\":\"regex\",\"pattern\":\"z+\"}", "\"abc\"", "array")]
    public async Task ExtractionTypesIncludeMissingValues(string settings, string input, string expected)
    {
        await VerifyAsync(new ExtractNode(), settings, Value(input), expected).ConfigureAwait(true);
    }

    /// <summary>対応表の出力と、項目内のパス参照を区別して推測します。</summary>
    /// <returns>変換処理の実行結果との照合です。</returns>
    [Theory]
    [InlineData("{\"operation\":\"concat\"}", "[\"a\",\"b\"]", "string")]
    [InlineData("{\"operation\":\"map\",\"mapping\":[{\"from\":1,\"to\":true},{\"from\":2,\"to\":\"two\"}]}", "1", "boolean")]
    [InlineData("{\"operation\":\"map\",\"mapping\":[{\"from\":1,\"to\":true},{\"from\":2,\"to\":\"two\"}]}", "2", "string")]
    [InlineData("{\"operation\":\"create\",\"fields\":{\"name\":{\"$path\":\"/name\"},\"values\":[{\"$path\":\"/id\"}]}}", "{\"name\":\"a\",\"id\":42}", "object")]
    public async Task TransformationTypesMatchActualValues(string settings, string input, string expected)
    {
        await VerifyAsync(new TransformNode(), settings, Value(input), expected).ConfigureAwait(true);
    }

    /// <summary>実行前の定義から推測し、後から実ノードの出力と比較します。</summary>
    /// <returns>外部通信を伴わない実行と検証です。</returns>
    private static async Task VerifyAsync(IActionNode node, string settings, NodeValue input, string expected)
    {
        JsonElement configuration = JsonSerializer.Deserialize<JsonElement>(settings);
        NodeDefinition definition = new("node", node.Descriptor.Type, configuration)
        {
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = input },
        };
        WorkflowTypeInference inference = new(new("workflow", "workflow", [definition], []), [node.Descriptor]);
        ValueTypeDefinition type = Assert.IsType<ValueTypeDefinition>(inference.PortType("node", "value", true));
        Assert.Equal(expected, type.Type);
        NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(settings, input)).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        NodeValue actual = result.Outputs["value"];
        if (actual.Kind == EValueKind.Binary)
        {
            Assert.Equal("binary", type.Type);
            return;
        }
        if (actual.Json.ValueKind == JsonValueKind.Null)
        {
            Assert.True(type.Optional);
            return;
        }
        ValueTypeDefinition observed = Assert.IsType<ValueTypeDefinition>(ValueTypeInference.FromJson(actual.Json, 0));
        Assert.Equal(observed.Type, type.Type);
        Assert.True(ValueTypeInference.CanAssign(type, observed));
        Assert.True(JsonElement.DeepEquals(configuration, definition.Settings));
    }

    /// <summary>検証用の独立したJSON値を作ります。</summary>
    /// <returns>入力値です。</returns>
    private static NodeValue Value(string json) => NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json));
}
