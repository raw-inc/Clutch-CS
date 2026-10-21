using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>保持区間の共有履歴がメモリキャッシュ枠を超えても、同じ本文を繰り返し復元しません。</summary>
[Collection("SnapshotCost"), Trait("Category", "Performance")]
public sealed class ReceiptInventoryCostTests(ITestOutputHelper output)
{
    /// <summary>履歴件数を増やして照合の割当と時間を測り、再オープン後も同じ指紋を返します。</summary>
    /// <returns>初回・再照合・新Repositoryの費用と、照合時に永続書込みをしない確認です。</returns>
    [Theory]
    [InlineData(4096)]
    [InlineData(40000)]
    [InlineData(210000)]
    public async Task RetainedReceiptsDoNotRequireRepeatedBodyReadsAsync(int count)
    {
        string root = Path.GetFullPath("../../../../../.work/performance/" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "state.db");
        SqliteRepository store = new(path);
        DurableReceipt last = Seed(path, count);
        List<object> measurements = [];
        string[]? expected = null;
        using SqliteConnection observation = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await observation.OpenAsync().ConfigureAwait(true);
        using SqliteCommand version = observation.CreateCommand();
        version.CommandText = "PRAGMA data_version";
        object? originalVersion = await version.ExecuteScalarAsync().ConfigureAwait(true);
        for (int pass = 0; pass < 3; pass++)
        {
            if (pass == 2)
            {
                store = new(path);
                originalVersion = await version.ExecuteScalarAsync().ConfigureAwait(true);
            }
            long before = GC.GetTotalAllocatedBytes(true);
            Stopwatch watch = Stopwatch.StartNew();
            FrozenSnapshotExport frozen = Assert.IsType<FrozenSnapshotExport>(await SnapshotExport.FreezeAsync(store).ConfigureAwait(true));
            string[] hashes = SyncInventory.Describe(frozen.Description);
            double elapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
            long allocatedBytes = GC.GetTotalAllocatedBytes(true) - before;
            Assert.Equal(count, frozen.Description.Receipts.Count);
            Assert.Equal(ContractJson.Hash(last), frozen.Description.Receipts.Single(receipt => receipt.OperationId == last.OperationId).Hash());
            if (expected is not null)
            {
                Assert.Equal(expected, hashes);
            }
            expected = hashes;
            Assert.Equal(originalVersion, await version.ExecuteScalarAsync().ConfigureAwait(true));
            measurements.Add(new { pass, allocatedBytes, elapsedMilliseconds });
            Assert.InRange(allocatedBytes, 0, count * 1600L + 1048576);
        }
        string report = JsonSerializer.Serialize(new { count, payloadBytesPerReceipt = 1536, databaseBytes = new FileInfo(path).Length, measurements });
        await File.WriteAllTextAsync(Path.Combine(root, "receipt-inventory.json"), report).ConfigureAwait(true);
        output.WriteLine(root);
        output.WriteLine(report);
    }

    /// <summary>通常の保存処理へ渡す履歴だけを準備し、準備費用は照合の計測へ含めません。</summary>
    /// <returns>実際に照合される最後の履歴です。</returns>
    private static DurableReceipt Seed(string path, int count)
    {
        DurableReceipt? last = null;
        for (int offset = 0; offset < count; offset += 512)
        {
            using SqliteSession session = new(path);
            RepositoryDocument rows = new();
            for (int index = offset; index < Math.Min(count, offset + 512); index++)
            {
                Guid id = Guid.CreateVersion7();
                DurableEntry entry = new("file:reservations:slots", (index % 80).ToString(System.Globalization.CultureInfo.InvariantCulture), id.ToString("N"),
                    NodeValue.FromObject(new { id, path = new string('x', 1536), index }));
                last = new(id, ContractJson.Hash(index), EOutcome.Succeeded, [entry]);
                rows.Receipts.Add(id, last);
            }
            session.Save(rows);
            session.Commit();
        }
        return last!;
    }
}
