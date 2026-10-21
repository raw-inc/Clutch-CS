using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>準備待ちの費用を、無関係な保持履歴の量と切り離して計測します。</summary>
[Collection("Performance")]
public sealed class SqlitePreparationScaleTests(ITestOutputHelper output)
{
    /// <summary>履歴を千・一万・五万件へ増やし、旧本文復元と現在の確認を同じSQLiteで比較します。</summary>
    /// <returns>強制GCを使わない割当・CPU・論理I/O・時間と、同じ指紋の維持です。</returns>
    [Fact, Trait("Category", "Performance")]
    public async Task WaitingCostDoesNotGrowWithRetainedHistoryAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        Dictionary<Guid, string> pending = [];
        for (int index = 0; index < 4; index++)
        {
            Guid id = Guid.NewGuid();
            string key = index.ToString(CultureInfo.InvariantCulture);
            pending.Add(id, (await repository.PrepareAsync(id, [new("workflow:prepared", key, null, NodeValue.FromObject(new string('x', 524288)))])).Fingerprint);
        }
        int previous = 0;
        foreach (int retained in new[] { 1000, 10000, 50000 })
        {
            SeedHistory(path, previous, retained);
            previous = retained;
            SqlitePreparationCache cache = new();
            foreach ((Guid id, string fingerprint) in pending)
            {
                Assert.Equal(fingerprint, cache.Read(path, id, CancellationToken.None));
            }
            SqliteCurrentVersion version = new(path, preparedCache: cache);
            string expected = version.Read(CancellationToken.None);
            Measurement legacy = Measure(() =>
            {
                using SqliteSession session = new(path, write: false);
                RepositoryDocument document = new();
                session.LoadPrepared(document);
                Assert.Equal(pending.Keys.Order(), document.Prepared.Keys.Order());
            });
            Measurement current = Measure(() =>
            {
                foreach ((Guid id, string fingerprint) in pending)
                {
                    Assert.Equal(fingerprint, cache.Read(path, id, CancellationToken.None));
                }
                Assert.Equal(expected, version.Read(CancellationToken.None));
            });
            output.WriteLine(JsonSerializer.Serialize(new { retained, databaseBytes = new FileInfo(path).Length, pending = pending.Count,
                payloadBytes = 4 * 524288, repetitions = 30, legacy, current, lastNaturalGcHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes }));
            Assert.InRange(current.Allocated, 0, legacy.Allocated / 8);
            Assert.InRange(current.Milliseconds, 0, legacy.Milliseconds * 2 + 100);
            Assert.InRange(current.Allocated, 0, 4194304);
            if (current.Written is { } written)
            {
                Assert.InRange(written, 0, 65536);
            }
        }
    }

    /// <summary>一回の比較中は、製品と同じ小さい接続と読取り取引を反復します。</summary>
    /// <returns>試験processの差分です。ディスクの物理I/Oではありません。</returns>
    private static Measurement Measure(Action read)
    {
        read();
        ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int index = 0; index < 30; index++)
        {
            read();
        }
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
        return new(watch.Elapsed.TotalMilliseconds, allocated, after.CpuMilliseconds - before.CpuMilliseconds,
            after.ReadBytes - before.ReadBytes, after.WriteBytes - before.WriteBytes, after.PrivateBytes);
    }

    /// <summary>試験所有のDBへだけ、実際に復元可能な過去診断行を段階的に追加します。</summary>
    private static void SeedHistory(string path, int first, int count)
    {
        using SqliteConnection connection = new("Pooling=False;Data Source=" + path);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO records(kind,key,scope,payload,hash) VALUES('entry',$key,'peer:previous:workflow-results',$payload,$hash)";
        command.Parameters.Add("$key", SqliteType.Text);
        command.Parameters.Add("$payload", SqliteType.Text);
        command.Parameters.Add("$hash", SqliteType.Text);
        NodeValue value = NodeValue.FromObject(new { detail = new string('h', 1024) });
        for (int index = first; index < count; index++)
        {
            DurableEntry entry = new("peer:previous:workflow-results", index.ToString("D8", CultureInfo.InvariantCulture), "fixture", value);
            string payload = JsonSerializer.Serialize(entry);
            command.Parameters["$key"].Value = JsonSerializer.Serialize(new[] { entry.Scope, entry.Key });
            command.Parameters["$payload"].Value = payload;
            command.Parameters["$hash"].Value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>一つの測定境界の結果です。</summary>
    private sealed record Measurement(double Milliseconds, long Allocated, double CpuMilliseconds, long? Read, long? Written, long PrivateBytes);
}
