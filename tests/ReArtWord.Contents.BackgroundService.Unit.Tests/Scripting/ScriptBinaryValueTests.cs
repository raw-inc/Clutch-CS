using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>JSON内のBinary境界で、所有権と既存Base64の許容範囲を維持します。</summary>
public sealed class ScriptBinaryValueTests
{
    /// <summary>空白・JSONエスケープも扱い、元JSONの寿命から独立したバイトを返します。</summary>
    [Theory]
    [InlineData("\"AQID\"")]
    [InlineData("\" A Q I D\\r\\n\\t\"")]
    [InlineData("\"\\u0041QID\"")]
    public void BinaryResultOwnsItsDecodedBytes(string encodedJson)
    {
        ValueTypeDefinition type = new("binary");
        NodeValue result;
        using (JsonDocument document = JsonDocument.Parse("{\"value\":" + encodedJson + ",\"valueKind\":\"binary\"}"))
        {
            result = ScriptBinaryValues.Result(document.RootElement, type);
            JsonElement original = document.RootElement.GetProperty("value");
            Assert.Equal(original.GetString(), ScriptBinaryValues.Argument(NodeValue.FromJson(original), type).Json.GetString());
        }
        Assert.Equal(EValueKind.Binary, result.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Binary.ToArray());
    }

    /// <summary>不正なBase64を文字列や空配列へ読み替えません。</summary>
    [Theory]
    [InlineData("AQI")]
    [InlineData("not base64!")]
    public void RejectsMalformedBinaryWithoutChangingTheContract(string encoded)
    {
        JsonElement response = JsonSerializer.SerializeToElement(new { value = encoded, valueKind = "binary" });
        ValueTypeDefinition type = new("binary");
        Assert.Throws<FormatException>(() => ScriptBinaryValues.Result(response, type));
        Assert.Throws<FormatException>(() => ScriptBinaryValues.Argument(NodeValue.FromObject(encoded), type));
    }
}
