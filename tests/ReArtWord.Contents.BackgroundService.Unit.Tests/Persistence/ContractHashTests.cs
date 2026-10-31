using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>一時配列を減らしても既存契約の内容識別子を変えません。</summary>
// 再利用時の割当量を計測するため、共有バッファーを他の試験が借用しない状態で実行します。
[Collection("Performance")]
public sealed class ContractHashTests
{
    /// <summary>プロパティ順、配列順、数値表現、nullを既存canonical JSONどおり扱います。</summary>
    [Theory]
    [InlineData("{\"z\":2,\"a\":1}", "{\"a\":1,\"z\":2}")]
    [InlineData("{\"b\":[2,{\"z\":null,\"a\":1.00}],\"a\":true}", "{\"a\":true,\"b\":[2,{\"a\":1.00,\"z\":null}]}")]
    [InlineData("null", "null")]
    [InlineData("{\"b\":0,\"a\":1,\"a\":2}", "{\"a\":1,\"a\":2,\"b\":0}")]
    [InlineData("{\"z\":{},\"B\":[],\"a\":{\"b\":2,\"A\":1}}", "{\"B\":[],\"a\":{\"A\":1,\"b\":2},\"z\":{}}")]
    [InlineData("{\"value\":\"<日本語>\"}", "{\"value\":\"\\u003C\\u65E5\\u672C\\u8A9E\\u003E\"}")]
    public void CanonicalHashRetainsTheExistingWireBytes(string input, string canonical)
    {
        using JsonDocument document = JsonDocument.Parse(input);
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        Assert.Equal(expected, ContractJson.Hash(document.RootElement));
    }

    /// <summary>局所領域と借用配列のどちらでも、大きい同名項目の順序を保持します。</summary>
    /// <param name="count">オブジェクトの項目数です。</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(32)]
    [InlineData(1000)]
    public void PropertySortRetainsDuplicateOrderAcrossBufferSizes(int count)
    {
        (string Name, int Value)[] properties = Enumerable.Range(0, count).Select(index => ("key" + (count - index) % 13, index)).ToArray();
        string input = "{" + string.Join(',', properties.Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value)) + "}";
        string canonical = "{" + string.Join(',', properties.OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value)) + "}";
        using JsonDocument document = JsonDocument.Parse(input);
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        Assert.Equal(expected, ContractJson.Hash(document.RootElement));
    }

    /// <summary>大きな値の繰返しハッシュで、出力全体を毎回新しいバイト配列へ複製しません。</summary>
    [Fact]
    public void RepeatedLargeHashesReuseSerializationBuffers()
    {
        using JsonDocument document = JsonDocument.Parse("{\"payload\":\"" + new string('x', 4194304) + "\"}");
        string expected = ContractJson.Hash(document.RootElement);
        _ = ContractJson.Hash(document.RootElement);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 3; index++)
        {
            Assert.Equal(expected, ContractJson.Hash(document.RootElement));
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 524288);
    }
}
