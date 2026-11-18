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

/// <summary>日次件数を縮小し、実経過3年分の本文保持と削除済みIDの圧縮を分けて測ります。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class ExactRetirementCycleTests(ITestOutputHelper output)
{
    /// <summary>連続して完了したIDは小さい区間へまとめ、途中の欠番・未完了操作は3年後も新規受付禁止へ含めません。</summary>
    /// <returns>保持中の本文量、退役記録量、資源使用量と再起動後の結果です。</returns>
    [Fact]
    public async Task ThreeYearsKeepRetainedBodiesAndContiguousProofBoundedAsync()
    {
        string root = Path.GetFullPath("../../../../../.work/performance/exact-retention-" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "state.db");
        SqliteRepository repository = new(path);
        using HistoryArchiveService archive = new(repository);
        DataRetentionSettings policy = new(7, 30, TimeZoneId: "UTC");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset firstDay = new DateTimeOffset(now.Year, now.Month, now.Day, 12, 0, 0, TimeSpan.Zero).AddDays(-1095);
        OperationIdSequence shared = new(), executions = new();
        Guid firstOperation = shared.Next(), missing = Guid.Empty, pending = Guid.Empty;
        string? version = null;
        List<object> measurements = [];
        List<long> retainedCounts = [], usedBytes = [];
        int[] milestones = [7, 30, 60, 90, 365, 730, 1095];
        ProcessResourceSample initial = ProcessResourceSample.Capture(Environment.ProcessId);
        long allocated = GC.GetTotalAllocatedBytes(true);
        Stopwatch watch = Stopwatch.StartNew();
        await archive.MaintainAsync(new(), policy, firstDay).ConfigureAwait(true);
        for (int day = 1; day <= 1095; day++)
        {
            DateTimeOffset at = firstDay.AddDays(day - 1);
            if (day == 500)
            {
                missing = shared.Next();
                pending = shared.Next();
                Assert.Equal(EOutcome.Succeeded, (await repository.PrepareAsync(pending, [new(NodeStateScope.Cluster, "pending", null, NodeValue.FromObject(500))]).ConfigureAwait(true)).Outcome);
            }
            Guid operation = day == 1 ? firstOperation
                                      : shared.Next();
            IReadOnlyList<DurableMutation> changes = await repository.StampAsync([new(NodeStateScope.Cluster, "cue", version, NodeValue.FromObject(day))]).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(operation, changes).ConfigureAwait(true)).Outcome);
            version = operation.ToString("N");
            await WriteDayAsync(repository, executions, at).ConfigureAwait(true);
            ObserveAt(path, at);
            for (int pass = 0; pass < 3; pass++)
            {
                await archive.MaintainAsync(new(BatchRecords: 1000), policy, at.AddHours(11)).ConfigureAwait(true);
            }
            if (milestones.Contains(day))
            {
                long retained = Scalar(path, "SELECT (SELECT COUNT(*) FROM history_candidates)+(SELECT COUNT(*) FROM history_index)");
                long ranges = Scalar(path, "SELECT COUNT(*) FROM retired_id_ranges");
                long markers = Scalar(path, "SELECT (SELECT COUNT(*) FROM retired_keys)+(SELECT COUNT(*) FROM retired_operations)");
                long used = Scalar(path, "SELECT (page_count-freelist_count)*page_size FROM pragma_page_count(),pragma_freelist_count(),pragma_page_size()");
                if (60 <= day)
                {
                    retainedCounts.Add(retained);
                    usedBytes.Add(used);
                    Assert.InRange(retained, 1, 33 * 45);
                    Assert.InRange(ranges, 1, 8);
                    Assert.InRange(markers, 0, 100);
                }
                ProcessResourceSample current = ProcessResourceSample.Capture(Environment.ProcessId);
                measurements.Add(new { day, retained, ranges, markers, usedBytes = used, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, cpuMilliseconds = current.CpuMilliseconds - initial.CpuMilliseconds,
                    processWriteBytes = current.WriteBytes - initial.WriteBytes, initial, current });
                await File.WriteAllTextAsync(Path.Combine(root, "retention-cycles.json"), JsonSerializer.Serialize(new { simulatedDays = 1095, measurements })).ConfigureAwait(true);
                output.WriteLine(FormattableString.Invariant($"day={day}, retained={retained}, ranges={ranges}, markers={markers}, usedBytes={used}"));
            }
        }
        Assert.InRange(retainedCounts.Max() - retainedCounts.Min(), 0, 90);
        Assert.InRange(usedBytes.Max() - usedBytes.Min(), 0, 2 * 1024 * 1024);
        SqliteRepository reopened = new(path);
        Assert.Equal(1095, (await reopened.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal("Persistence.HistoryExpired", (await reopened.ReadReceiptAsync(firstOperation).ConfigureAwait(true))!.Reason);
        Assert.Equal(EOutcome.Unknown, (await reopened.ApplyBatchAsync(firstOperation, [new(NodeStateScope.Cluster, "cue", version, NodeValue.FromObject(-1))]).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await reopened.ApplyBatchAsync(missing, [new(NodeStateScope.Cluster, "late", null, NodeValue.FromObject(7))]).ConfigureAwait(true)).Outcome);
        Assert.True(await reopened.HasPendingWriteAsync(NodeStateScope.Cluster, "pending").ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await reopened.CommitAsync(pending).ConfigureAwait(true)).Outcome);
        Assert.Equal(500, (await reopened.ReadAsync(NodeStateScope.Cluster, "pending").ConfigureAwait(true))!.Value!.Json.GetInt32());
        output.WriteLine(root);
    }

    /// <summary>現在の記録形式で、期限付き応答24件と完了したファイル復旧記録8件を作ります。</summary>
    /// <returns>実保存APIによる一日分の確定です。</returns>
    private static async Task WriteDayAsync(SqliteRepository repository, OperationIdSequence executions, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        string scope = BoundedRequestReceipts.Prefix + "receipts:fixture";
        Task<DurableEntry?>[] writes = Enumerable.Range(0, 24).Select(item =>
        {
            DateTimeOffset source = at.AddDays(item % 2 == 0 ? -31
                                                             : 31);
            RequestReceipt receipt = new("fixture", Guid.Empty, source.AddMinutes(5), new(EOutcome.Succeeded, NodeValue.FromObject(item)), SavedAt: at);
            return RequestReceiptPersistence.WriteAsync(repository, scope, Guid.CreateVersion7(source).ToString("N"), null, NodeValue.FromObject(receipt), cancellationToken).AsTask();
        }).ToArray();
        Assert.All(await Task.WhenAll(writes).ConfigureAwait(false), Assert.NotNull);
        LocalSnapshotWrite[] records = Enumerable.Range(0, 8).Select(_ => new LocalSnapshotWrite("peer:fixture:file-recovery", executions.Next().ToString("N"),
            NodeValue.FromObject(new { execution = new { Outcome = EOutcome.Succeeded }, release = new { Outcome = EOutcome.Succeeded }, CompletedAt = at }))).ToArray();
        Assert.Equal(EOutcome.Succeeded, await repository.WriteSnapshotsAsync(records, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>時計注入のないDB内部記録だけを、試験専用保存先の論理日に揃えます。</summary>
    private static void ObserveAt(string path, DateTimeOffset at)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE history_candidates SET recorded_at=$at,eligible_after=0 WHERE reviewed_hash IS NULL";
        command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    /// <summary>試験用DBの件数と使用容量を読み取ります。</summary>
    /// <returns>呼出元で固定した集計値です。</returns>
    // 抑制理由: この試験内の固定SQLだけを実行し、外部入力を連結しません。
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
