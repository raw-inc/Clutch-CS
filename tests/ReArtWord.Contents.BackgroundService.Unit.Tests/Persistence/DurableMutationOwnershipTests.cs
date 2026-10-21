using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>保存候補の高速な所有権分離が、本文・型・競合候補・バイト列の混線を起こさないことを確認します。</summary>
public sealed class DurableMutationOwnershipTests(ITestOutputHelper output)
{
    /// <summary>呼出元の文書寿命と可変コレクションを切り離し、元の内容指紋を維持します。</summary>
    [Theory]
    [InlineData(EValueKind.Json)]
    [InlineData(EValueKind.Binary)]
    public void CallerMutationAndDisposalDoNotChangeOwnedContent(EValueKind kind)
    {
        byte[] bytes = [1, 2, 3];
        string[] candidates = ["original"];
        Dictionary<string, ValueTypeDefinition> properties = new(StringComparer.Ordinal) { ["number"] = new("int64") };
        DurableMutation[] owned;
        string expected;
        using (JsonDocument document = JsonDocument.Parse("{\"value\":42}"))
        {
            NodeValue value = new(kind, document.RootElement, bytes) { DataType = new("object", Properties: properties, Schema: document.RootElement) };
            DurableMutation[] source = [new("configuration", "value", "previous", value, candidates, "causal"), new("configuration", "delete", "old", null)];
            expected = ContractJson.Hash(source);
            owned = DurableMutationOwnership.Copy(source);
            source[0] = new("different", "different", null, NodeValue.Null);
        }
        bytes[0] = 99;
        candidates[0] = "different";
        properties["number"] = new("string");
        Assert.Equal(expected, ContractJson.Hash(owned));
        Assert.Equal(kind, owned[0].Value!.Kind);
        Assert.Equal("original", Assert.Single(owned[0].ResolveVersions!));
        Assert.Equal(1, owned[0].Value!.Binary.Span[0]);
        Assert.Equal("int64", owned[0].Value!.DataType!.Properties!["number"].Type);
        Assert.Null(owned[1].Value);
    }

    /// <summary>すでに所有している大きなJSON本文を保存境界ごとに直列化・復元しません。</summary>
    [Fact]
    public void LargeOwnedJsonAvoidsSerializationRoundTrips()
    {
        DurableMutation[] source = [new("workflows", "large", null, NodeValue.FromObject(new string('x', 2097152)))];
        _ = ContractJson.Copy(source);
        _ = DurableMutationOwnership.Copy(source);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        DurableMutation[] previous = ContractJson.Copy(source);
        double oldMilliseconds = watch.Elapsed.TotalMilliseconds;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        DurableMutation[] current = DurableMutationOwnership.Copy(source);
        double milliseconds = watch.Elapsed.TotalMilliseconds;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(ContractJson.Hash(previous), ContractJson.Hash(current));
        Assert.InRange(allocatedBytes, 0, 65536);
        output.WriteLine(JsonSerializer.Serialize(new { payloadCharacters = 2097152, oldMilliseconds, oldBytes, milliseconds, allocatedBytes }));
    }
}
