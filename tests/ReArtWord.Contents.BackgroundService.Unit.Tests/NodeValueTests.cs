using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests;

/// <summary>Peer間の値の転送でバイト列と数値を失わないことを検証します。</summary>
public sealed class NodeValueTests
{
    /// <summary>元バッファの変更とJSON転送後もバイト列を保持します。</summary>
    [Fact]
    public void BinaryPayloadSurvivesSerializationAndSourceMutation()
    {
        byte[] source = [0, 127, 128, 255];
        NodeValue value = NodeValue.FromBytes(source);
        source[0] = 42;
        string serialized = JsonSerializer.Serialize(value);
        NodeValue restored = JsonSerializer.Deserialize<NodeValue>(serialized)!;

        Assert.Equal(EValueKind.Binary, restored.Kind);
        Assert.Equal(new byte[] { 0, 127, 128, 255 }, restored.Binary.ToArray());
    }

    /// <summary>JSON文書の破棄後も倍精度で表せない整数を保持します。</summary>
    [Fact]
    public void JsonPayloadOwnsItsDocumentAndPreservesIntegerPrecision()
    {
        NodeValue value;
        using (JsonDocument source = JsonDocument.Parse("""{"id":9007199254740993,"items":[null,1]}"""))
        {
            value = NodeValue.FromJson(source.RootElement);
        }
        NodeValue restored = JsonSerializer.Deserialize<NodeValue>(JsonSerializer.Serialize(value))!;

        Assert.Equal(9007199254740993L, restored.Json.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, restored.Json.GetProperty("items")[0].ValueKind);
    }

    /// <summary>入力済みnullと未入力の出力ポートを区別します。</summary>
    [Fact]
    public void SuppliedNullIsDistinctFromAnAbsentPort()
    {
        NodeResult nullResult = NodeResult.Success(NodeValue.Null);
        NodeResult noOutput = NodeResult.Success();

        Assert.Equal(JsonValueKind.Null, nullResult.Outputs["value"].Json.ValueKind);
        Assert.False(noOutput.Outputs.ContainsKey("value"));
    }
}
