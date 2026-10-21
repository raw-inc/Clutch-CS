using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>日次件数を縮小して1095回の保持境界を跨ぎ、累積年数と保持中の量を区別して測ります。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class RetentionCyclePerformanceTests(ITestOutputHelper output)
{
    /// <summary>現在Stateを維持し、確定済みの要求・ファイル・実行本文と時刻付き削除索引を循環させます。</summary>
    /// <returns>期間中の容量、資源、固定回数の読取り費用と、期限切れ要求の拒否です。</returns>
    [Fact]
    public async Task ThreeYearsOfDailyRetentionKeepBodiesAndTimeBasedMarkersBoundedAsync()
    {
        string root = Path.GetFullPath("../../../../../.work/performance/retention-" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "state.db");
        SqliteRepository repository = new(path);
        using HistoryArchiveService archive = new(repository);
        DataRetentionSettings policy = new(7, 30, TimeZoneId: "UTC");
        DateTimeOffset firstDay = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(-1095);
        Guid firstOperation = Guid.CreateVersion7(firstDay.AddHours(12));
        string? version = null;
        List<object> measurements = [];
        List<long> retainedCounts = [];
        List<long> usedBytes = [];
        int[] milestones = [7, 30, 60, 90, 365, 730, 1095];
        ProcessResourceSample initial = ProcessResourceSample.Capture(Environment.ProcessId);
        long allocated = GC.GetTotalAllocatedBytes(true);
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int day = 1; day <= 1095; day++)
        {
            DateTimeOffset at = firstDay.AddDays(day - 1).AddHours(12);
            Guid operation = day == 1 ? firstOperation
                                      : Guid.CreateVersion7(at);
            IReadOnlyList<DurableMutation> mutation = await repository.StampAsync([new(NodeStateScope.Cluster, "cue", version, NodeValue.FromObject(day))]).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(operation, mutation).ConfigureAwait(true)).Outcome);
            version = operation.ToString("N");
            await WriteRequestsAsync(repository, at).ConfigureAwait(true);
            await repository.WriteSnapshotsAsync(Day(at)).ConfigureAwait(true);
            SetNewCandidateTime(path, at);
            for (int pass = 0; pass < 3; pass++)
            {
                await archive.MaintainAsync(new(BatchRecords: 1000), policy, at.AddHours(11)).ConfigureAwait(true);
            }
            if (milestones.Contains(day))
            {
                object measurement = await MeasureAsync(repository, path, day, elapsed, allocated, initial).ConfigureAwait(true);
                measurements.Add(measurement);
                long retained = Scalar(path, "SELECT (SELECT COUNT(*) FROM history_candidates)+(SELECT COUNT(*) FROM history_index)");
                long used = Scalar(path, "SELECT (page_count-freelist_count)*page_size FROM pragma_page_count(),pragma_freelist_count(),pragma_page_size()");
                if (60 <= day)
                {
                    retainedCounts.Add(retained);
                    usedBytes.Add(used);
                    Assert.InRange(retained, 1, 32 * 45);
                    Assert.InRange(Scalar(path, "SELECT (SELECT COUNT(*) FROM retired_keys)+(SELECT COUNT(*) FROM retired_operations)"), 0, 100);
                }
                string report = JsonSerializer.Serialize(new { simulatedDays = day, rawDays = 7, totalDays = 30,
                    sampledPerDay = new { relayReceipts = 24, audioFileOperations = 8, otherExecutions = 4, stateWrites = 1 },
                    fullWorkloadPerDay = new { relay = 300000, audio = 100, otherPerWorkflow = 50, stateReads = 20000, stateWrites = 3000 }, measurements });
                await File.WriteAllTextAsync(Path.Combine(root, "retention-cycles.json"), report).ConfigureAwait(true);
                output.WriteLine(FormattableString.Invariant($"day={day}, retained={retained}, usedBytes={used}"));
            }
        }
        Assert.InRange(retainedCounts.Max() - retainedCounts.Min(), 0, 90);
        Assert.InRange(usedBytes.Max() - usedBytes.Min(), 0, 2 * 1024 * 1024);
        SqliteRepository reopened = new(path);
        Assert.Equal(1095, (await reopened.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal("Persistence.HistoryExpired", (await reopened.ReadReceiptAsync(firstOperation).ConfigureAwait(true))!.Reason);
        Assert.NotEqual(EOutcome.Succeeded, (await reopened.ApplyBatchAsync(firstOperation, [new(NodeStateScope.Cluster, "cue", version, NodeValue.FromObject(-1))]).ConfigureAwait(true)).Outcome);
        Assert.Equal(1095, (await reopened.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!.Value!.Json.GetInt32());
        output.WriteLine(root);
    }

    /// <summary>実行時と同じ履歴形式を使い、件数だけを縮小した一日分を用意します。</summary>
    /// <returns>循環対象の要求、ファイル終了、実行診断です。</returns>
    private static List<LocalSnapshotWrite> Day(DateTimeOffset at)
    {
        List<LocalSnapshotWrite> rows = [];
        for (int item = 0; item < 8; item++)
        {
            string key = Guid.CreateVersion7(at).ToString("N");
            rows.Add(new("peer:fixture:file-gate-requests", key, NodeValue.FromObject(new { Outcome = EOutcome.Succeeded, CompletedAt = at })));
            rows.Add(new("peer:fixture:file-recovery", key, NodeValue.FromObject(new { execution = new { Outcome = EOutcome.Succeeded }, release = new { Outcome = EOutcome.Succeeded }, CompletedAt = at })));
        }
        for (int item = 0; item < 4; item++)
        {
            rows.Add(new("peer:fixture:workflow-results", Guid.CreateVersion7(at).ToString("N"), NodeValue.FromObject(new { CompletedAt = at, detail = new string('x', 2048) })));
        }
        return rows;
    }

    /// <summary>要求の確定記録は局所診断APIへ混ぜず、実行時と同じ耐久CASで保存します。</summary>
    /// <returns>一日分を小さい一括取引へまとめた保存です。</returns>
    private static async Task WriteRequestsAsync(SqliteRepository repository, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        Task<DurableEntry?>[] writes = Enumerable.Range(0, 24).Select(item =>
        {
            RequestReceipt request = new("fixture", Guid.Empty, at.AddMinutes(5), new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(item)));
            return RequestReceiptPersistence.WriteAsync(repository, "receipts:fixture", Guid.CreateVersion7(at).ToString("N"), null, NodeValue.FromObject(request), cancellationToken).AsTask();
        }).ToArray();
        Assert.All(await Task.WhenAll(writes).ConfigureAwait(false), Assert.NotNull);
    }

    /// <summary>実保存APIに時計注入がないため、このfixtureの未分類行だけの記録時刻を論理日に揃えます。</summary>
    private static void SetNewCandidateTime(string path, DateTimeOffset at)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE history_candidates SET recorded_at=$at,eligible_after=0 WHERE reviewed_hash IS NULL";
        command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    /// <summary>本文を展開せず、小さいDB統計と固定1000回の現在値読取りを測定します。</summary>
    /// <returns>累積資源と、同じ作業量の区間測定です。</returns>
    private static async Task<object> MeasureAsync(SqliteRepository repository, string path, int day, Stopwatch total, long allocated,
        ProcessResourceSample initial, CancellationToken cancellationToken = default)
    {
        ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
        long readAllocated = GC.GetTotalAllocatedBytes(true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int read = 0; read < 1000; read++)
        {
            Assert.Equal(day, (await repository.ReadAsync(NodeStateScope.Cluster, "cue", cancellationToken).ConfigureAwait(true))!.Value!.Json.GetInt32());
        }
        ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
        watch.Stop();
        readAllocated = GC.GetTotalAllocatedBytes(true) - readAllocated;
        Assert.Equal(before.WriteBytes, after.WriteBytes);
        Dictionary<string, long> counts = [];
        foreach (string table in new[] { "records", "history_candidates", "history_index", "history_blocks", "retention_deadlines", "history_events", "history_checkpoints", "retired_operations", "retired_keys" })
        {
            counts[table] = Scalar(path, "SELECT COUNT(*) FROM " + table);
        }
        return new { day, counts, databaseBytes = new FileInfo(path).Length,
            usedBytes = Scalar(path, "SELECT (page_count-freelist_count)*page_size FROM pragma_page_count(),pragma_freelist_count(),pragma_page_size()"),
            freeBytes = Scalar(path, "SELECT freelist_count*page_size FROM pragma_freelist_count(),pragma_page_size()"),
            elapsedMilliseconds = total.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            cpuMilliseconds = after.CpuMilliseconds - initial.CpuMilliseconds, processWriteBytes = after.WriteBytes - initial.WriteBytes,
            currentRead1000Milliseconds = watch.Elapsed.TotalMilliseconds, readAllocatedBytes = readAllocated,
            gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2), before, after };
    }

    /// <summary>試験自身が作ったDBに対し、固定された集計SQLだけを実行します。</summary>
    /// <returns>件数またはバイト数です。</returns>
    // 抑制理由: private呼出元の固定SQLと固定テーブル名だけを受け、ユーザー入力を連結しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "試験内の固定された集計SQLだけを実行します。")]
    private static long Scalar(string path, string sql)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
