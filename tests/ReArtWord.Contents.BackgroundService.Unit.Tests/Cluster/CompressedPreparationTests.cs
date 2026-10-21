using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>準備本文の可逆圧縮を、通常形式と同じ内容・容量・一回性の境界で検証します。</summary>
public sealed class CompressedPreparationTests
{
    /// <summary>大きな定義を小さく運び、元の操作IDと変更内容を完全に復元します。</summary>
    [Fact]
    public void LargePreparationPreservesOriginalOperationAndContent()
    {
        ReplicationRequest request = CreateRequest();
        ReplicationPreparationPayload payload = ReplicationPreparationPayload.Create(request, 1048576);
        Assert.NotNull(payload.Compressed);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(payload.Compressed.Value).Length, 1, payload.Length / 10);
        ReplicationRequest restored = ReplicationPreparationPayload.Read(payload.Compressed.Value, 1048576);
        Assert.Equal(request.Id, restored.Id);
        Assert.Equal(ContractJson.Hash(request), ContractJson.Hash(restored));
        Assert.Equal(ContractJson.Hash(request), ContractJson.Hash(payload.Plain.Deserialize<ReplicationRequest>()));
    }

    /// <summary>小さな要求と展開上限外の要求は既存の通常形式だけを使用します。</summary>
    [Fact]
    public void SmallOrOversizedPreparationDoesNotUseCompression()
    {
        ReplicationRequest small = new(Guid.NewGuid(), [new("configuration", "small", null, NodeValue.FromObject(1))]);
        Assert.Null(ReplicationPreparationPayload.Create(small, 1048576).Compressed);
        Assert.Null(ReplicationPreparationPayload.Create(CreateRequest(), 1024).Compressed);
    }

    /// <summary>圧縮封筒とBase64の追加分で小さくならない本文は通常形式を維持します。</summary>
    [Fact]
    public void IncompressibleBinaryKeepsTheOriginalPayload()
    {
        byte[] data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32768);
        ReplicationRequest request = new(Guid.NewGuid(), [new("configuration", "binary", null, NodeValue.FromBytes(data))]);
        ReplicationPreparationPayload payload = ReplicationPreparationPayload.Create(request, 1048576);
        Assert.Null(payload.Compressed);
        Assert.Equal(ContractJson.Hash(request), ContractJson.Hash(payload.Plain.Deserialize<ReplicationRequest>()));
    }

    /// <summary>過大展開、切断、余剰データ、内容違いを通常prepareへ渡しません。</summary>
    [Theory]
    [InlineData("length")]
    [InlineData("limit")]
    [InlineData("hash")]
    [InlineData("identity")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    public void InvalidCompressedPreparationIsRejected(string damage)
    {
        ReplicationPreparationPayload payload = ReplicationPreparationPayload.Create(CreateRequest(), 1048576);
        JsonObject packet = JsonNode.Parse(payload.Compressed!.Value.GetRawText())!.AsObject();
        byte[] data = Convert.FromBase64String(packet["Data"]!.GetValue<string>());
        switch (damage)
        {
            case "length": packet["Length"] = payload.Length + 1; break;
            case "limit": packet["Length"] = int.MaxValue; break;
            case "hash": packet["Hash"] = "wrong"; break;
            case "identity": packet["Id"] = Guid.NewGuid(); break;
            case "truncated": packet["Data"] = JsonValue.Create(data[..^1]); break;
            case "trailing": packet["Data"] = JsonValue.Create(data.Concat(new byte[] { 0 }).ToArray()); break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        Assert.Throws<InvalidDataException>(() => ReplicationPreparationPayload.Read(JsonSerializer.SerializeToElement(packet), 1048576));
    }

    /// <summary>接続先や秘密値を持たない大きな設定変更です。</summary>
    /// <returns>元IDを固定した準備要求です。</returns>
    private static ReplicationRequest CreateRequest() => new(Guid.NewGuid(), [new("configuration", "large", null, NodeValue.FromObject(new string('x', 131072)))]);
}
