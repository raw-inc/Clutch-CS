using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests.Performance;

/// <summary>実SQLite・予約・過去版を組み合わせ、小さい更新が無関係な履歴量へ比例しないことを検証します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class RepositoryPerformanceTests(ITestOutputHelper output)
{
    /// <summary>準備済み操作がある状態へ8回の小さい差分を取り込み、値・割当・書込み量を確認します。</summary>
    /// <returns>過去本文を全復元・再保存しない取込みの測定です。</returns>
    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(256)]
    public async Task SmallImportsStayBoundedWithPendingWritesAndLargeHistoryAsync(int historyCount)
    {
        historyCount = Scale(historyCount);
        string root = CreateDirectory();
        SqliteRepository target = new(Path.Combine(root, "target.db")), source = new(Path.Combine(root, "source.db"));
        await SeedAsync(target, historyCount).ConfigureAwait(true);
        List<RepositorySnapshot> changes = [];
        string? version = null;
        for (int index = 0; index < 8; index++)
        {
            DurableReceipt receipt = await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:small", "value", version, NodeValue.FromObject(index))]).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, receipt.Outcome);
            version = Assert.Single(receipt.Entries).Version;
            changes.Add(await source.ExportAsync().ConfigureAwait(true));
        }
        Guid pending = Guid.NewGuid();
        IReadOnlyList<DurableMutation> preparation = await target.StampAsync([new("workflow:pending", "value", null, NodeValue.FromObject(42))]).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await target.PrepareAsync(pending, preparation).ConfigureAwait(true)).Outcome);
        try
        {
            ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
            long allocated = GC.GetTotalAllocatedBytes(true);
            Stopwatch elapsed = Stopwatch.StartNew();
            foreach (RepositorySnapshot change in changes)
            {
                Assert.True(await target.ImportAsync(change).ConfigureAwait(true));
            }
            elapsed.Stop();
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
            Report(root, "small-import", historyCount, elapsed, allocated, before, after);
            Assert.InRange(allocated, 0, 16L * 1024 * 1024);
            if (after.WriteBytes is long written && before.WriteBytes is long initial)
            {
                Assert.InRange(written - initial, 0, 8L * 1024 * 1024);
            }
            Assert.Equal(7, (await target.ReadAsync("workflow:small", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
            Assert.True(await target.HasPendingWriteAsync("workflow:pending", "value").ConfigureAwait(true));
            Assert.Equal(16384, (await target.ReadAsync("workflow:history", "value").ConfigureAwait(true))!.Value!.GetString()!.Length);
            SqliteRepository reopened = new(Path.Combine(root, "target.db"));
            Assert.Equal(await target.GetSharedVersionAsync().ConfigureAwait(true), await reopened.GetSharedVersionAsync().ConfigureAwait(true));
        }
        finally
        {
            await target.AbortPreparedAsync(pending).ConfigureAwait(true);
        }
    }

    /// <summary>多数の過去版と大きいローカル診断があっても、共有版確認だけでは本文を複製・書込みしません。</summary>
    /// <returns>50回の実DB読取りの時間・割当・プロセスI/Oです。</returns>
    [Theory]
    [InlineData(16)]
    [InlineData(256)]
    public async Task RepeatedObservationDoesNotRewriteUnchangedHistoryAsync(int historyCount)
    {
        historyCount = Scale(historyCount);
        string root = CreateDirectory();
        SqliteRepository repository = new(Path.Combine(root, "state.db"));
        await SeedAsync(repository, historyCount).ConfigureAwait(true);
        await repository.WriteLocalSnapshotAsync("peer:fixture:diagnostics", "payload", NodeValue.FromObject(new string('d', 4194304))).ConfigureAwait(true);
        string expected = await repository.GetSharedVersionAsync().ConfigureAwait(true);
        ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
        long allocated = GC.GetTotalAllocatedBytes(true);
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int index = 0; index < 50; index++)
        {
            Assert.Equal(expected, await repository.GetSharedVersionAsync().ConfigureAwait(true));
        }
        elapsed.Stop();
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
        Report(root, "observe", historyCount, elapsed, allocated, before, after);
        Assert.InRange(allocated, 0, 4L * 1024 * 1024);
        if (after.WriteBytes is long written && before.WriteBytes is long initial)
        {
            Assert.InRange(written - initial, 0, 65536);
        }
    }

    /// <summary>現在値だけでなく異なる過去版を正式な永続APIで積み上げます。</summary>
    /// <returns>測定対象外のfixture準備です。</returns>
    private static async Task SeedAsync(SqliteRepository repository, int count, CancellationToken cancellationToken = default)
    {
        string? version = null;
        for (int index = 0; index < count; index++)
        {
            NodeValue value = NodeValue.FromObject(index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + new string('h', 16376));
            IReadOnlyList<DurableMutation> stamped = await repository.StampAsync([new("workflow:history", "value", version, value)], cancellationToken).ConfigureAwait(false);
            DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.NewGuid(), stamped, cancellationToken).ConfigureAwait(false);
            Assert.Equal(EOutcome.Succeeded, receipt.Outcome);
            version = Assert.Single(receipt.Entries).Version;
        }
    }

    /// <summary>各試行を固有の隔離保存先へ置き、既存データを参照しません。</summary>
    /// <returns>試験専用の絶対パスです。</returns>
    private static string CreateDirectory()
    {
        string root = Path.GetFullPath("../../../../../.work/performance/" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>通常CIと同じ処理を最大16,384履歴まで増やし、不正な指定で小さい試験へ黙って戻しません。</summary>
    /// <returns>既定1倍、明示時1〜64倍の履歴数です。</returns>
    private static int Scale(int count)
    {
        string? configured = Environment.GetEnvironmentVariable("CONTENTS_PERFORMANCE_HISTORY_SCALE");
        if (configured is null)
        {
            return count;
        }
        int scale = int.Parse(configured, System.Globalization.CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, 64);
        return checked(count * scale);
    }

    /// <summary>成否とは別に実測値を保存し、CIの失敗時にも比較できるようにします。</summary>
    private void Report(string root, string scenario, int historyCount, Stopwatch elapsed, long allocated, ProcessResourceSample before, ProcessResourceSample after)
    {
        string report = JsonSerializer.Serialize(new { scenario, historyCount, allocatedBytes = allocated, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            cpuMilliseconds = after.CpuMilliseconds - before.CpuMilliseconds, processReadBytes = after.ReadBytes - before.ReadBytes, processWriteBytes = after.WriteBytes - before.WriteBytes,
            before, after });
        File.WriteAllText(Path.Combine(root, "measurement.json"), report);
        output.WriteLine(report);
        output.WriteLine(root);
    }
}
