using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>診断保存の圧縮で実行詳細を失わず、旧形式と破損境界を確認します。</summary>
public sealed class WorkflowHistoryCodecTests
{
    /// <summary>繰り返すノード出力を縮小し、成否・Unicode・Binary・明示型を完全復元します。</summary>
    [Theory]
    [InlineData(EOutcome.Succeeded)]
    [InlineData(EOutcome.Failed)]
    [InlineData(EOutcome.Unknown)]
    public void DetailsRoundTripAndLegacyReadersKeepTheOutcome(EOutcome outcome)
    {
        WorkflowHistoryRecord original = Record(outcome);
        WorkflowHistoryRecord encoded = WorkflowHistoryCodec.Encode(original);
        NodeValue header = ContractJson.ToValue(encoded), payload = ContractJson.ToValue(encoded.Payload);
        Assert.True(header.PayloadSize() + payload.PayloadSize() < ContractJson.ToValue(original).PayloadSize() / 2);
        WorkflowHistoryRecord stored = header.Json.Deserialize<WorkflowHistoryRecord>(ContractJson.Options)! with { Payload = payload.Json.Deserialize<WorkflowHistoryPayload>(ContractJson.Options) };
        Assert.NotNull(stored.Payload);
        WorkflowHistoryRecord decoded = WorkflowHistoryCodec.Decode(stored);
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(decoded));
        Assert.Null(decoded.Payload);
        LegacyRecord legacy = header.Json.Deserialize<LegacyRecord>(ContractJson.Options)!;
        Assert.Equal(original.Sequence, legacy.Sequence);
        Assert.Equal(original.View.Report!.Id, WorkflowExecutionJournal.Identity(legacy.View));
        Assert.Equal(outcome, legacy.View.Report?.Outcome ?? legacy.View.Summary!.Outcome);
    }

    /// <summary>既存の非圧縮記録は変更せず読め、小さい結果を無理に圧縮しません。</summary>
    [Fact]
    public void OldAndSmallRecordsKeepTheirShape()
    {
        WorkflowHistoryRecord original = Record(EOutcome.Succeeded);
        NodeValue old = ContractJson.ToValue(new LegacyRecord(original.Sequence, original.View));
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(WorkflowHistoryCodec.Decode(old.Json.Deserialize<WorkflowHistoryRecord>(ContractJson.Options)!)));
        WorkflowHistoryRecord small = original with { View = new(original.View.Report! with { Nodes = [] }) };
        NodeValue encoded = ContractJson.ToValue(WorkflowHistoryCodec.Encode(small));
        Assert.False(encoded.Json.TryGetProperty("payload", out _));
        Assert.Equal(ContractJson.Hash(small), ContractJson.Hash(encoded.Json.Deserialize<WorkflowHistoryRecord>(ContractJson.Options)));
    }

    /// <summary>巨大展開・改変・余分な末尾・別実行の要約を拒否します。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void InvalidCompressedDetailsAreRejected(int mutation)
    {
        WorkflowHistoryRecord stored = WorkflowHistoryCodec.Encode(Record(EOutcome.Succeeded));
        WorkflowHistoryPayload payload = stored.Payload!;
        stored = mutation switch
        {
            0 => stored with { Payload = payload with { Length = int.MaxValue } },
            1 => stored with { Payload = payload with { Hash = "invalid" } },
            2 => stored with { Payload = payload with { Data = [.. payload.Data, 0] } },
            3 => stored with { Payload = payload with { Format = "unknown" } },
            4 => stored with { View = stored.View with { Summary = stored.View.Summary! with { Id = Guid.NewGuid() } } },
            6 => stored with { Payload = payload with { CompletedAt = payload.CompletedAt.AddDays(1) } },
            _ => stored with { Payload = payload with { Data = payload.Data[..^1] } },
        };
        Assert.Throws<InvalidDataException>(() => WorkflowHistoryCodec.Decode(stored));
    }

    /// <summary>一秒内の複数結果を元の順序・型・バイト列のまま復元します。</summary>
    [Fact]
    public void BlockRoundTripsEveryResultAndLeavesAnExplicitLegacySummary()
    {
        WorkflowHistoryRecord[] records = BlockRecords();
        WorkflowHistoryBlock packed = Assert.IsType<WorkflowHistoryBlock>(WorkflowHistoryCodec.Pack(records));
        NodeValue payload = ContractJson.ToValue(packed);
        WorkflowHistoryBlock stored = payload.Json.Deserialize<WorkflowHistoryBlock>(ContractJson.Options)!;
        ExecutionResultView[] views = WorkflowHistoryCodec.Unpack(stored);
        Assert.Equal(records.Select(record => ContractJson.Hash(record.View)), views.Select(ContractJson.Hash));
        Assert.True(payload.PayloadSize() < records.Sum(record => ContractJson.ToValue(record).PayloadSize()) / 4);
        WorkflowHistoryRecord header = WorkflowHistoryCodec.BlockHeader(records[^1]);
        LegacyRecord legacy = ContractJson.ToValue(header).Json.Deserialize<LegacyRecord>(ContractJson.Options)!;
        Assert.Equal(records[^1].Sequence, legacy.Sequence);
        Assert.Equal(WorkflowExecutionJournal.Identity(records[^1].View), WorkflowExecutionJournal.Identity(legacy.View));
        Assert.Equal(WorkflowHistoryCodec.BlockReason, legacy.View.DetailReason);
        Assert.Equal(ContractJson.Hash(records[^1].View), ContractJson.Hash(WorkflowHistoryCodec.Restore(header, stored, views).View));
    }

    /// <summary>圧縮しない単件と、保持日の境界や保存世代を跨ぐ不正グループを区別します。</summary>
    [Fact]
    public void BlocksDoNotCrossRetentionOrSequenceBoundaries()
    {
        WorkflowHistoryRecord[] records = BlockRecords();
        Assert.Null(WorkflowHistoryCodec.Pack([records[0]]));
        Assert.Throws<InvalidDataException>(() => WorkflowHistoryCodec.Pack([records[0], records[2]]));
        WorkflowHistoryRecord changed = records[1] with { View = new(records[1].View.Report! with { CompletedAt = records[1].View.Report!.CompletedAt.AddSeconds(1) }) };
        Assert.Throws<InvalidDataException>(() => WorkflowHistoryCodec.Pack([records[0], changed]));
        Assert.Throws<InvalidDataException>(() => WorkflowHistoryCodec.Pack([records[0] with { Sequence = long.MaxValue }, records[1] with { Sequence = long.MinValue }]));
        WorkflowHistoryRecord failure = Record(EOutcome.Failed);
        Assert.Null(WorkflowHistoryCodec.Pack([records[0], failure]));
        Assert.Throws<InvalidDataException>(() => WorkflowHistoryCodec.BlockHeader(failure));
    }

    /// <summary>巨大展開・欠損・別の保持境界・重複IDなどを読み込まず、破損を成功扱いしません。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void InvalidBlocksAreRejected(int mutation)
    {
        WorkflowHistoryBlock block = WorkflowHistoryCodec.Pack(BlockRecords())!;
        block = mutation switch
        {
            0 => block with { Length = int.MaxValue },
            1 => block with { Hash = "invalid" },
            2 => block with { Data = [.. block.Data, 0] },
            3 => block with { Format = "unknown" },
            4 => block with { Count = block.Count + 1 },
            5 => block with { Data = block.Data[..^1] },
            6 => block with { CompletedAt = block.CompletedAt.AddSeconds(1) },
            8 => block with { Data = new byte[65537] },
            9 => block with { CompletedAt = block.CompletedAt.AddMilliseconds(1) },
            _ => block with { FirstSequence = long.MaxValue },
        };
        Assert.Throws<InvalidDataException>(() => WorkflowHistoryCodec.Unpack(block));
    }

    /// <summary>ブロックが正常でも、循環スロットを再利用した別結果へ結び付けません。</summary>
    [Fact]
    public void BlocksRejectDuplicateIdentitiesAndMismatchedHeaders()
    {
        WorkflowHistoryRecord[] records = BlockRecords();
        WorkflowHistoryBlock block = WorkflowHistoryCodec.Pack(records)!;
        ExecutionResultView[] views = WorkflowHistoryCodec.Unpack(block);
        WorkflowHistoryRecord header = WorkflowHistoryCodec.BlockHeader(records[^1]);
        Assert.Throws<InvalidDataException>(() => WorkflowHistoryCodec.Restore(header with { Sequence = 1 }, block, views));
        WorkflowHistoryRecord mismatched = header with { View = new(null, WorkflowHistoryCodec.Header(header.View) with { Id = Guid.NewGuid() }) };
        Assert.Throws<InvalidDataException>(() => WorkflowHistoryCodec.Restore(mismatched, block, views));
        records[1] = records[1] with { View = records[0].View };
        WorkflowHistoryBlock duplicate = WorkflowHistoryCodec.Pack(records)!;
        Assert.Throws<InvalidDataException>(() => WorkflowHistoryCodec.Unpack(duplicate));
    }

    /// <summary>圧縮後も大きいBinary群を一ブロックへ蓄積せず、個別形式へ戻します。</summary>
    [Fact]
    public void IncompressibleGroupsFallBackToIndividualRecords()
    {
        DateTimeOffset now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        WorkflowHistoryRecord[] records = Enumerable.Range(1, 20).Select(index =>
        {
            NodeResult result = NodeResult.Success(NodeValue.FromBytes(RandomNumberGenerator.GetBytes(24000)));
            ExecutionReport report = new(Guid.NewGuid(), "flow", "revision", EOutcome.Succeeded, now, now,
                [new("binary", EOutcome.Succeeded, [new("a", result)])]);
            return new WorkflowHistoryRecord(index, new(report));
        }).ToArray();
        Assert.Null(WorkflowHistoryCodec.Pack(records));
    }

    /// <summary>日付境界の直前でも全件が同じ保持時刻区間へ属する結果です。</summary>
    /// <returns>成功だけをまとめた連続世代です。</returns>
    private static WorkflowHistoryRecord[] BlockRecords()
    {
        DateTimeOffset now = new(2026, 10, 8, 23, 59, 59, TimeSpan.Zero);
        return [Record(EOutcome.Succeeded, now, 42), Record(EOutcome.Succeeded, now.AddMilliseconds(100), 43), Record(EOutcome.Succeeded, now.AddMilliseconds(200), 44)];
    }

    /// <summary>複数ノードへ同じ値が渡る通常の実行結果を構築します。</summary>
    /// <returns>従来の64KiB上限に収まる診断です。</returns>
    private static WorkflowHistoryRecord Record(EOutcome outcome, DateTimeOffset? at = null, long sequence = 42)
    {
        DateTimeOffset now = at ?? DateTimeOffset.UtcNow;
        NodeValue text = NodeValue.FromObject(string.Concat(Enumerable.Repeat("音声生成 / input.eventPending / stable data 🌐 ", 70)));
        NodeValue number = NodeValue.FromObject(7L) with { DataType = new("int64") };
        NodeResult result = new(outcome, "success", new Dictionary<string, NodeValue>
        {
            ["text"] = text, ["number"] = number, ["binary"] = NodeValue.FromBytes(new byte[] { 0, 255, 7 }),
        });
        ExecutionReport report = new(Guid.NewGuid(), "flow", "revision", outcome, now, now.AddMilliseconds(10),
            Enumerable.Range(0, 4).Select(index => new NodeExecutionReport("node-" + index, outcome, [new("peer", result)])).ToArray());
        return new(sequence, new(report));
    }

    /// <summary>圧縮詳細を知らない旧版の保存契約です。</summary>
    private sealed record LegacyRecord(long Sequence, ExecutionResultView View);
}
