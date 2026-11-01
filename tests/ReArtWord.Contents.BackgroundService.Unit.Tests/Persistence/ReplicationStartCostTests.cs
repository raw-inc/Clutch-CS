using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>同じ準備・確定結果を保ったまま、実SQLiteの書込み取引数と時間を比較します。</summary>
[Collection("Performance")]
public sealed class ReplicationStartCostTests(ITestOutputHelper output)
{
    /// <summary>反復保存のジャーナル作成回数を観測し、耐久保存の回数削減を確認します。</summary>
    /// <returns>従来順序と一括開始の実時間・割当量・書込み取引数です。</returns>
    [Fact]
    public async Task BatchedStartReducesDurableTransactionsWithoutDroppingReceipts()
    {
        Measurement before = await MeasureAsync(false).ConfigureAwait(true);
        Measurement after = await MeasureAsync(true).ConfigureAwait(true);
        Assert.Equal(48, before.Journals);
        Assert.Equal(24, after.Journals);
        output.WriteLine(JsonSerializer.Serialize(new { before, after }));
    }

    /// <summary>同じ大きさの現在値を反復更新し、書込みごとのrollback journalを数えます。</summary>
    /// <returns>計測後も確定値と操作記録が一致している保存の計測値です。</returns>
    private static async Task<Measurement> MeasureAsync(bool atomic)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", new(EStorageBackend.Sqlite)).ConfigureAwait(false);
        string payload = new('x', 262144);
        string? version = await SaveAsync(repository, atomic, payload, null, CancellationToken.None).ConfigureAwait(false);
        int journals = 0;
        using FileSystemWatcher watcher = new(files.Root, "state.db-journal") { NotifyFilter = NotifyFilters.FileName };
        Exception? observationFailure = null;
        watcher.Error += (_, args) => Interlocked.Exchange(ref observationFailure, args.GetException());
        watcher.Deleted += (_, _) => Interlocked.Increment(ref journals);
        watcher.EnableRaisingEvents = true;
        long allocated = GC.GetTotalAllocatedBytes(true);
        Stopwatch watch = Stopwatch.StartNew();
        for (int index = 0; index < 12; index++)
        {
            version = await SaveAsync(repository, atomic, payload + index, version, CancellationToken.None).ConfigureAwait(false);
        }
        double milliseconds = watch.Elapsed.TotalMilliseconds;
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        int expected = 48;
        if (atomic)
        {
            expected = 24;
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref journals) < expected)
        {
            Assert.Null(Volatile.Read(ref observationFailure));
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
        watcher.EnableRaisingEvents = false;
        Assert.Null(Volatile.Read(ref observationFailure));
        Assert.Equal(payload + 11, (await repository.ReadAsync("workflows", "w").ConfigureAwait(false))!.Value!.GetString());
        return new(milliseconds, allocated, journals);
    }

    /// <summary>一回の準備・コミットを同じ要求内容と同じ成功条件で行います。</summary>
    /// <returns>次の比較保存に使う確定版です。</returns>
    private static async Task<string> SaveAsync(IDurableRepository repository, bool atomic, string value, string? version, CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflows", "w", version, NodeValue.FromObject(value))];
        DurableMutation[] metadata = [new("cluster:replication-members", id.ToString("N"), null, NodeValue.FromObject("a")),
            new("cluster:operations", id.ToString("N"), null, NodeValue.FromObject("pending"))];
        if (atomic)
        {
            ReplicationStartResult started = await ReplicationStart.BeginAsync(repository, id, mutations, metadata, cancellationToken).ConfigureAwait(false);
            Assert.Equal(EOutcome.Succeeded, started.Initialized.Outcome);
            Assert.Equal(EOutcome.Succeeded, started.Prepared?.Outcome);
        }
        else
        {
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), metadata, cancellationToken).ConfigureAwait(false)).Outcome);
            IReadOnlyList<DurableMutation> stamped = await repository.StampAsync(mutations, cancellationToken).ConfigureAwait(false);
            Assert.Equal(EOutcome.Succeeded, (await repository.PrepareAsync(id, stamped, cancellationToken).ConfigureAwait(false)).Outcome);
        }
        DurableReceipt committed = await repository.CommitAsync(id, cancellationToken).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, committed.Outcome);
        Assert.Equal(committed.Fingerprint, (await repository.ReadReceiptAsync(id, cancellationToken).ConfigureAwait(false))!.Fingerprint);
        return Assert.Single(committed.Entries).Version;
    }

    /// <summary>同じ12回の更新と耐久確認に必要だった実測です。</summary>
    private sealed record Measurement(double Milliseconds, long AllocatedBytes, int Journals);
}
