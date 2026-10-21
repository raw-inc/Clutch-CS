using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>段階同期の存在確認が大きな過去本文を毎回読まないことを、実SQLiteとプロセスI/Oで確認します。</summary>
[Collection("Performance")]
public sealed class RecoveryReceiptCostTests(ITestOutputHelper output)
{
    /// <summary>同じDB・同じ結果で、索引ありと旧索引の本文フォールバックを比較します。</summary>
    /// <returns>本文に比例しない読取り、書込みのない照会、および同じ存在証明です。</returns>
    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    public async Task RecoveryInventoryAvoidsReadingHistoricalPayloadsAsync(int count)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        NodeValue payload = NodeValue.FromObject(new string('x', 65536));
        DurableReceipt[] receipts = Enumerable.Range(0, count).Select(index => new DurableReceipt(Guid.NewGuid(), "matching-fingerprint", EOutcome.Succeeded,
            [new("workflow:history", "value", index.ToString(System.Globalization.CultureInfo.InvariantCulture), payload)])).ToArray();
        Assert.True(await repository.ImportAsync(new([new("workflow:history", "value", "current", NodeValue.FromObject(42))], receipts)));
        RecoveryRepository recovery = RecoveryRepository.Create(repository)!;
        DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(2);
        await recovery.ReadAsync("source", now);
        Measurement indexed = await MeasureAsync(recovery, now, count);
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE receipt_fingerprints SET operation_fingerprint=NULL,outcome=NULL,reason=NULL";
            Assert.Equal(count, await command.ExecuteNonQueryAsync());
        }
        Measurement legacy = await MeasureAsync(recovery, now, count);
        Assert.Equal(indexed.References, legacy.References);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { count, bodyBytes = count * 65536L, indexed, legacy }));
        if (indexed.ReadBytes is not null && legacy.ReadBytes is not null)
        {
            Assert.True(indexed.ReadBytes * 2 < legacy.ReadBytes, "The recovery inventory still reads historical bodies.");
        }
        Assert.InRange(indexed.AllocatedBytes, 0, count * 8192L + 1048576);
        if (indexed.WriteBytes is not null)
        {
            Assert.InRange(indexed.WriteBytes.Value, 0, 65536);
        }
    }

    /// <summary>旧writerが索引を欠落させても、少量の背景レビューで通常経路へ戻ります。</summary>
    /// <returns>新旧索引の混在、本文照合と補完後の同一結果です。</returns>
    [Fact]
    public async Task LegacyMetadataIsBackfilledByBoundedReviewAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        for (int index = 0; index < 4; index++)
        {
            await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:history", index.ToString(System.Globalization.CultureInfo.InvariantCulture), null, NodeValue.FromObject(index))]);
        }
        RecoveryRepository recovery = RecoveryRepository.Create(repository)!;
        DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(2);
        RepositorySnapshot before = await recovery.ReadAsync("source", now);
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE receipt_fingerprints SET operation_fingerprint=NULL,outcome=NULL,reason=NULL";
            Assert.Equal(4, await command.ExecuteNonQueryAsync());
        }
        Assert.Equal(ContractJson.Hash(before), ContractJson.Hash(await recovery.ReadAsync("source", now)));
        using (SqliteSession session = new(path))
        {
            SqliteHistoryScan.Review(session, 2, now);
            session.Commit();
        }
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM receipt_fingerprints WHERE operation_fingerprint IS NOT NULL AND outcome IS NOT NULL";
            Assert.Equal(2L, await command.ExecuteScalarAsync());
        }
        Assert.Equal(ContractJson.Hash(before), ContractJson.Hash(await recovery.ReadAsync("source", now)));
    }

    /// <summary>DB準備・結果書出し・強制GCを含めず、同じ存在確認を測定します。</summary>
    /// <returns>一回の一覧作成で増えた実プロセスの使用量です。</returns>
    private static async Task<Measurement> MeasureAsync(RecoveryRepository repository, DateTimeOffset now, int count, CancellationToken cancellationToken = default)
    {
        ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
        long allocated = GC.GetTotalAllocatedBytes(true);
        Stopwatch watch = Stopwatch.StartNew();
        RepositorySnapshot snapshot = await repository.ReadAsync("source", now, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
        Assert.Equal(count, snapshot.DeferredHistory!.Count);
        Assert.Empty(snapshot.Receipts);
        return new(watch.Elapsed.TotalMilliseconds, after.CpuMilliseconds - before.CpuMilliseconds, allocated,
            after.ReadBytes - before.ReadBytes, after.WriteBytes - before.WriteBytes, after.PrivateBytes, ContractJson.Hash(snapshot.DeferredHistory));
    }

    /// <summary>測定境界を固定した時間・割当・論理I/Oと内容照合です。</summary>
    private sealed record Measurement(double Milliseconds, double CpuMilliseconds, long AllocatedBytes, long? ReadBytes, long? WriteBytes, long PrivateBytes, string References);
}
