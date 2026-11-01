using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>一時本文を削減しても、長さ・検証・コピー後の所有権を維持します。</summary>
// 再利用時の割当量を計測するため、共有バッファーを他の試験が借用しない状態で実行します。
[Collection("Performance")]
public sealed class JsonAllocationTests
{
    /// <summary>空白、Unicode、エスケープ、数値の元表記もUTF-8の長さへ含めます。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{ \"text\" : \"日本語🌐\", \"number\":1.00, \"escape\":\"\\u0061\" }")]
    [InlineData("[true, false, 1234567890123456789]")]
    public void PayloadLengthRetainsRawJsonRepresentation(string json)
    {
        using JsonDocument source = JsonDocument.Parse(json);
        NodeValue value = NodeValue.FromJson(source.RootElement);
        Assert.Equal(Encoding.UTF8.GetByteCount(value.Json.GetRawText()), value.PayloadSize());
        JsonSerializerOptions options = new(ContractJson.Options) { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1, NewLine = "\r\n" };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(value, options).LongLength, JsonSize.Measure(value, options));
    }

    /// <summary>BinaryのBase64表現や分割書込みでも、同じJSONの長さを一回の列挙で数えます。</summary>
    [Fact]
    public void CountingIncludesBinaryAndEnumeratesValuesOnce()
    {
        NodeValue[] values = [NodeValue.FromBytes(new byte[100000]), NodeValue.FromObject("改行\n引用\""), NodeValue.Null];
        long expected = JsonSerializer.SerializeToUtf8Bytes(values, ContractJson.Options).LongLength;
        int enumerations = 0;
        IEnumerable<NodeValue> Enumerate()
        {
            enumerations++;
            foreach (NodeValue value in values)
            {
                yield return value;
            }
        }
        Assert.Equal(expected, JsonSize.Measure(Enumerate()));
        Assert.Equal(1, enumerations);
    }

    /// <summary>長さだけの確認で大きい本文を毎回新しい配列や文字列へ複製しません。</summary>
    [Fact]
    public void RepeatedSizeChecksUseBoundedTemporaryMemory()
    {
        NodeValue value = NodeValue.FromObject(new string('x', 4194304));
        long expected = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options).LongLength;
        _ = JsonSize.Measure(value);
        _ = JsonSize.Measure(value);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 3; index++)
        {
            Assert.Equal(expected, JsonSize.Measure(value));
            Assert.Equal(4194306, value.PayloadSize());
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 262144);
    }

    /// <summary>借用バッファー返却後にもJSONとBinaryが生存し、別コピーや元の変更と混ざりません。</summary>
    [Fact]
    public void CopiesOwnTheirValuesAfterSerializationBuffersAreReused()
    {
        byte[] bytes = [1, 2, 3];
        using JsonDocument source = JsonDocument.Parse("{\"value\":\"日本語\"}");
        Dictionary<string, NodeValue> input = new(StringComparer.Ordinal)
        {
            ["json"] = new(EValueKind.Json, source.RootElement, default),
            ["bytes"] = new(EValueKind.Binary, NodeValue.Null.Json, bytes),
        };
        Dictionary<string, NodeValue> first = ContractJson.Copy(input);
        bytes[0] = 99;
        input.Clear();
        source.Dispose();
        for (int index = 0; index < 10; index++)
        {
            _ = ContractJson.Copy(new Dictionary<string, string>(StringComparer.Ordinal) { ["different"] = new string('z', 100000) });
        }
        Assert.Equal("日本語", first["json"].Json.GetProperty("value").GetString());
        Assert.Equal(new byte[] { 1, 2, 3 }, first["bytes"].Binary.ToArray());
        Dictionary<string, NodeValue> second = ContractJson.Copy(first);
        second.Clear();
        Assert.Equal(2, first.Count);
    }

    /// <summary>pool上限を超える本文でも、小刻みな書込みごとに全体を複製し続けません。</summary>
    [Fact]
    public void CopyGrowthRemainsLinearBeyondTheRetainedBufferLimit()
    {
        string[] source = Enumerable.Repeat(new string('x', 1024), 4096).ToArray();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        string[] copied = ContractJson.Copy(source);
        long copiedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(source, copied);
        Assert.NotSame(source, copied);
        Assert.InRange(copiedBytes, 0, 32L * 1024 * 1024);
    }

    /// <summary>計測でもSerializerの深さ制限を迂回しません。</summary>
    [Fact]
    public void CountingPreservesSerializationFailures()
    {
        object value = new { first = new { second = new { third = 1 } } };
        JsonSerializerOptions options = new(ContractJson.Options) { MaxDepth = 2 };
        Assert.Throws<JsonException>(() => JsonSize.Measure(value, options));
        Assert.Throws<JsonException>(() => JsonSerializer.SerializeToUtf8Bytes(value, options));
    }
}
