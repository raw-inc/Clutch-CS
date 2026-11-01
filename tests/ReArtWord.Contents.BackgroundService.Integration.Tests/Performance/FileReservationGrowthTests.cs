using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests.Performance;

/// <summary>約3年分の終了履歴と保持期間を分離し、予約・書込み・解放の費用の増加傾向を測定します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class FileReservationGrowthTests(ITestOutputHelper output)
{
    /// <summary>実APIを呼ばず30回/日を模した履歴を用意し、3並列の実書込みと失われた解放確認の回収を行います。</summary>
    /// <returns>総履歴件数、保持本文数、現役予約数、割当、CPU、プロセスI/O、処理時間です。</returns>
    [Theory]
    [InlineData(330)]
    [InlineData(3300)]
    [InlineData(33000)]
    public async Task CompletedHistoryDoesNotAccumulateActiveReservationsOrPerWriteCostAsync(int runs)
    {
        string root = Path.GetFullPath("../../../../../.work/performance/" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "state.db");
        SqliteRepository repository = new(database);
        PeerCluster cluster = new(new(Guid.NewGuid().ToString("N"), "fixture", "host", "127.0.0.1", "127.255.255.255", CorePeer.AvailablePort(), 0, true, root), repository)
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
        using ClusterFileGate gate = new(cluster, repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = cluster.ConfigureAwait(true);
        await cluster.StartAsync().ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await SeedAsync(repository, runs, now).ConfigureAwait(true);
        List<object> measurements = [];
        await MeasureAsync(repository, cluster, gate, root, "before-retention", measurements).ConfigureAwait(true);
        using (HistoryArchiveService history = new(repository))
        {
            for (int pass = 0; pass < Math.Max(16, runs / 200); pass++)
            {
                await history.MaintainAsync(new(BatchRecords: 10000), new(7, 30), now).ConfigureAwait(true);
            }
        }
        long bodies = RetainedBodies(database);
        Assert.InRange(bodies, 0, 31 * 30 * 2 + 100);
        await MeasureAsync(repository, cluster, gate, root, "after-retention", measurements).ConfigureAwait(true);
        string report = JsonSerializer.Serialize(new { runs, simulatedDays = runs / 30.0, rawDays = 7, totalDays = 30, retainedFileHistoryBodies = bodies,
            databaseBytes = new FileInfo(database).Length, measurements });
        await File.WriteAllTextAsync(Path.Combine(root, "file-reservation-growth.json"), report).ConfigureAwait(true);
        output.WriteLine(root);
        output.WriteLine(report);
    }

    /// <summary>利用者が提示した一回500件・最大80予約の条件で、台帳保存の増幅を測ります。</summary>
    /// <returns>正常解放と確認喪失後の回収を含む高並列条件の観測です。</returns>
    [Fact]
    public async Task FiveHundredWritesWithEightyReservationsStayRecoverableAsync()
    {
        string root = Path.GetFullPath("../../../../../.work/performance/" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        SqliteRepository repository = new(Path.Combine(root, "state.db"));
        PeerCluster cluster = new(new(Guid.NewGuid().ToString("N"), "fixture", "host", "127.0.0.1", "127.255.255.255", CorePeer.AvailablePort(), 0, true, root), repository)
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
        using ClusterFileGate gate = new(cluster, repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = cluster.ConfigureAwait(true);
        await cluster.StartAsync().ConfigureAwait(true);
        List<object> measurements = [];
        await MeasureAsync(repository, cluster, gate, root, "500-files-80-active", measurements, 500, 80, 2).ConfigureAwait(true);
        string report = JsonSerializer.Serialize(new { filesPerWorkflow = 500, maximumConcurrentReservations = 80, measurements });
        await File.WriteAllTextAsync(Path.Combine(root, "file-concurrency.json"), report).ConfigureAwait(true);
        output.WriteLine(root);
        output.WriteLine(report);
    }

    /// <summary>各回の確認記録を正式な保存APIへ渡し、保持期間の評価時刻だけを早送りします。</summary>
    /// <returns>測定から分離した履歴準備です。</returns>
    private static async Task SeedAsync(SqliteRepository repository, int runs, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        for (int offset = 0; offset < runs; offset += 250)
        {
            List<LocalSnapshotWrite> rows = [];
            for (int index = offset; index < Math.Min(runs, offset + 250); index++)
            {
                string key = Guid.NewGuid().ToString("N");
                DateTimeOffset completed = now.AddDays(-(runs - 1 - index) / 30);
                rows.Add(new("peer:fixture:file-gate-requests", key, NodeValue.FromObject(new { Outcome = EOutcome.Succeeded, CompletedAt = completed })));
                rows.Add(new("peer:fixture:file-recovery", key, NodeValue.FromObject(new { execution = new { Outcome = EOutcome.Succeeded }, release = new { Outcome = EOutcome.Succeeded }, CompletedAt = completed })));
            }
            Assert.Equal(EOutcome.Succeeded, await repository.WriteSnapshotsAsync(rows, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>総履歴と無関係に、同時に3件だけを予約して実ファイルを書きます。</summary>
    /// <returns>現役予約をゼロへ戻した、固定回数の操作費用です。</returns>
    private static async Task MeasureAsync(SqliteRepository repository, PeerCluster cluster, ClusterFileGate gate, string root, string phase,
        List<object> measurements, int writes = 12, int concurrency = 3, int rounds = 3, CancellationToken cancellationToken = default)
    {
        PeerInfo member = Assert.Single(cluster.GetPeers());
        byte[] payload = new byte[65536];
        System.Security.Cryptography.RandomNumberGenerator.Fill(payload);
        for (int round = 0; round < rounds; round++)
        {
            ProcessResourceSample before = ProcessResourceSample.Capture(Environment.ProcessId);
            long allocated = GC.GetTotalAllocatedBytes(true);
            Stopwatch elapsed = Stopwatch.StartNew();
            for (int batch = 0; batch < writes; batch += concurrency)
            {
                string[] paths = Enumerable.Range(0, Math.Min(concurrency, writes - batch)).Select(index => Path.Combine(root, FormattableString.Invariant($"{phase}-{round}-{batch}-{index}.bin"))).ToArray();
                FileLeaseResult[] acquired = await Task.WhenAll(paths.Select(path => gate.AcquireAsync(Guid.NewGuid(),
                    [new(member.PeerId, member.InstanceId, member.HostId, path, true)], EFileConflictPolicy.Fail, cancellationToken))).ConfigureAwait(false);
                Assert.All(acquired, lease => Assert.Equal(EOutcome.Succeeded, lease.Outcome));
                await Task.WhenAll(paths.Select(path => File.WriteAllBytesAsync(path, payload, cancellationToken))).ConfigureAwait(false);
                if (round == 1)
                {
                    foreach (FileLeaseResult item in acquired)
                    {
                        await repository.WriteLocalSnapshotAsync("peer:fixture:file-recovery", item.Lease!.Id.ToString("N"), NodeValue.FromObject(new
                        {
                            lease = item.Lease, execution = NodeResult.Success(NodeValue.FromObject("written")), release = new FilePhaseResult(EOutcome.Unknown, "fixture.lostConfirmation"),
                        }), cancellationToken).ConfigureAwait(false);
                    }
                    Assert.True(await gate.RecoverEndedAsync(false, cancellationToken).ConfigureAwait(false));
                }
                else
                {
                    FilePhaseResult[] released = await Task.WhenAll(acquired.Select(item => gate.ReleaseAsync(item.Lease!, cancellationToken))).ConfigureAwait(false);
                    Assert.All(released, result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
                }
                Assert.Equal(0, await ActiveCountAsync(repository, cancellationToken).ConfigureAwait(false));
                foreach (string path in paths)
                {
                    Assert.Equal(payload, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                }
            }
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            ProcessResourceSample after = ProcessResourceSample.Capture(Environment.ProcessId);
            measurements.Add(new { phase, round, writes, concurrency, activeReservations = 0, milliseconds = elapsed.Elapsed.TotalMilliseconds, allocatedBytes = allocated,
                cpuMilliseconds = after.CpuMilliseconds - before.CpuMilliseconds, processWriteBytes = after.WriteBytes - before.WriteBytes, before, after });
            Assert.InRange(allocated, 0, Math.Max(64L * 1024 * 1024, writes * 12L * 1024 * 1024));
            if (before.WriteBytes is long initial && after.WriteBytes is long final)
            {
                Assert.InRange(final - initial, 0, Math.Max(64L * 1024 * 1024, writes * 12L * 1024 * 1024));
            }
        }
    }

    /// <summary>大きい履歴本文を読み出さず、圧縮済みと未圧縮の残数だけを確認します。</summary>
    /// <returns>ファイル関連の保持本文件数です。再実行防止の小さい索引は含めません。</returns>
    private static long RetainedBodies(string path)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM records WHERE kind='entry' AND scope IN ('peer:fixture:file-gate-requests','peer:fixture:file-recovery'))
                 + (SELECT COUNT(*) FROM history_index WHERE kind='entry' AND scope IN ('peer:fixture:file-gate-requests','peer:fixture:file-recovery'))
            """;
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>旧台帳と再利用スロットのどちらでも、現役予約だけを数えます。</summary>
    /// <returns>ファイルへの排他が残る件数です。</returns>
    private static async Task<int> ActiveCountAsync(SqliteRepository repository, CancellationToken cancellationToken = default)
    {
        DurableEntry? header = await repository.ReadAsync("file:reservations", "active", cancellationToken).ConfigureAwait(false);
        string format = "Format";
        if (header?.Value?.Json.TryGetProperty(format, out _) == true)
        {
            return (await repository.ListAsync("file:reservations:slots", cancellationToken).ConfigureAwait(false)).Count(entry => entry.Value is not null);
        }
        return header?.Value?.Json.EnumerateObject().Count() ?? 0;
    }
}
