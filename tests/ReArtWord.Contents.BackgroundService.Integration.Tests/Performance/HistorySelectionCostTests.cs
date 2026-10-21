using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests.Performance;

/// <summary>移行対象のない大きい履歴を、背景保守が毎回本文まで走査しないことを確認します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class HistorySelectionCostTests(ITestOutputHelper output)
{
    /// <summary>保持期間内の本文が増えても、期限索引で対象外と判定して通常処理のI/Oを抑えます。</summary>
    /// <returns>同じ8回の保守のCPU・プロセスI/O・経過時間です。</returns>
    [Theory]
    [InlineData(256)]
    [InlineData(4096)]
    public async Task RetainedRecentHistoryDoesNotCausePayloadScansAsync(int count)
    {
        string root = Path.GetFullPath("../../../../../.work/performance/history-selection-" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        SqliteRepository repository = new(Path.Combine(root, "state.db"));
        string content = new('x', 32768);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int offset = 0; offset < count; offset += 64)
        {
            List<DurableMutation> records = [];
            for (int index = offset; index < Math.Min(count, offset + 64); index++)
            {
                string key = Guid.CreateVersion7(now).ToString("N");
                NodeValue value = NodeValue.FromObject(new
                {
                    Fingerprint = key, InstanceId = Guid.NewGuid(), ExpiresAt = now.AddDays(-1),
                    Response = new { Outcome = EOutcome.Succeeded, Value = NodeValue.FromObject(content) },
                });
                records.Add(new("receipts:fixture", key, null, value));
            }
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.CreateVersion7(now), records).ConfigureAwait(true)).Outcome);
        }
        using HistoryArchiveService archive = new(repository);
        HistoryArchiveOptions options = new(KeepDays: 10000, MaximumHotRecords: 100000);
        DataRetentionSettings policy = new(RawDays: 10000, TotalDays: null);
        DateTimeOffset maintenance = now.AddMinutes(20);
        HistoryArchiveStatus status;
        int batches = 0;
        do
        {
            await archive.MaintainAsync(options, policy, maintenance).ConfigureAwait(true);
            status = await archive.ReadStatusAsync().ConfigureAwait(true);
            Assert.True(++batches < count + 100);
        }
        while (status.UnreviewedRecords != 0 || status.UnindexedRows != 0);
        Assert.Equal(0, status.ArchivedRecords);
        ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
        Stopwatch watch = Stopwatch.StartNew();
        for (int pass = 0; pass < 8; pass++)
        {
            Assert.Equal(0, await archive.MaintainAsync(options, policy, maintenance).ConfigureAwait(true));
        }
        ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
        long? read = after.ReadBytes - before.ReadBytes, write = after.WriteBytes - before.WriteBytes;
        output.WriteLine($"records={count}, elapsedMs={watch.Elapsed.TotalMilliseconds:F1}, cpuMs={after.CpuMilliseconds - before.CpuMilliseconds:F1}, readBytes={read}, writeBytes={write}");
        if (read.HasValue && write.HasValue)
        {
            Assert.InRange(read.Value, 0, 16777216);
            Assert.InRange(write.Value, 0, 1048576);
        }
        Assert.Equal(count, (await repository.ListAsync("receipts:fixture").ConfigureAwait(true)).Count);
    }
}
