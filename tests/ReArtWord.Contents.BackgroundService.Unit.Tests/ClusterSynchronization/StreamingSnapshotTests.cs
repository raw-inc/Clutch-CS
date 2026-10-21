using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>固定版・旧同期契約・本文を保持しない差分照合を検証します。</summary>
public sealed class StreamingSnapshotTests
{
    /// <summary>同じ履歴が一致している再照合では、大きい過去本文を再復元しません。</summary>
    /// <returns>旧方式と同じ区分、既知履歴に依存しない割当上限、再読取り後の内容です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedHistoryDoesNotAllocateItsPayloadAgainAsync(bool routed)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository;
        if (routed)
        {
            repository = await RepositoryFactory.CreateAsync(files.Root, "fixture", new()).ConfigureAwait(true);
        }
        else
        {
            repository = new SqliteRepository(Path.Combine(files.Root, "state.db"));
        }
        string? version = null;
        for (int index = 0; index < 64; index++)
        {
            DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.NewGuid(), [new("file:reservations", "active", version,
                NodeValue.FromObject(new string((char)('a' + index % 26), 65536))) ]).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, receipt.Outcome);
            version = Assert.Single(receipt.Entries).Version;
        }
        RepositorySnapshot previous = await repository.ExportAsync().ConfigureAwait(true);
        string[] expected = SyncInventory.Describe(previous);
        string[] first = await SnapshotExport.ReadAsync(repository, export => Task.FromResult(SyncInventory.Describe(export))).ConfigureAwait(true);
        Assert.Equal(expected, first);
        byte[] bytes = await SnapshotExport.ReadAsync(repository, export =>
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            string[] hashes = SyncInventory.Describe(export);
            Assert.Equal(expected, hashes);
            using MemoryStream output = new();
            SyncSnapshotWriter.Write(output, export, hashes, expected, CancellationToken.None);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 1048576);
            return Task.FromResult(output.ToArray());
        }).ConfigureAwait(true);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(SyncInventory.Metadata(previous)), bytes);
        Assert.Equal(64, (await repository.ExportAsync().ConfigureAwait(true)).Receipts.Count);
    }

    /// <summary>部分差分は元ファイルからのみ作り、履歴件数に比例した復号を行いません。</summary>
    /// <returns>旧形式との同値性と、コピー時の小さい割当量です。</returns>
    [Fact]
    public void NarrowingCopiesOnlySelectedBytesWithoutRehydratingHistory()
    {
        DurableEntry entry = new("workflow:fixture", "value", "version", NodeValue.FromObject(new string('x', 1048576)));
        RepositorySnapshot snapshot = new([entry], Enumerable.Range(0, 16).Select(_ => new DurableReceipt(Guid.NewGuid(), "fingerprint", EOutcome.Succeeded, [entry])).ToArray());
        SnapshotExport export = new(snapshot with { Receipts = [] }, snapshot.Receipts.Select(SnapshotReceipt.FromValue).ToArray());
        string[] hashes = SyncInventory.Describe(export);
        string[] known = hashes.ToArray();
        known[SyncInventory.ReceiptBucket(snapshot.Receipts[0].OperationId)] = new string('0', 64);
        using MemoryStream original = new();
        SyncSnapshotIndex index = SyncSnapshotWriter.Write(original, export, hashes, null, CancellationToken.None);
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(SyncInventory.Difference(snapshot, hashes, known));
        using MemoryStream difference = new(expected.Length);
        long before = GC.GetAllocatedBytesForCurrentThread();
        SyncSnapshotWriter.Copy(original, difference, index, hashes, known, CancellationToken.None);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 262144);
        Assert.Equal(expected, difference.ToArray());
    }

    /// <summary>一致する区分の本文にはアクセスせず、取消時にも次回の書込みへ排他を残しません。</summary>
    /// <returns>不要本文の非取得と取消後の媒体利用です。</returns>
    [Fact]
    public async Task CancellationReleasesTheSnapshotTransactionAsync()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "fixture", new()).ConfigureAwait(true);
        using CancellationTokenSource cancellation = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SnapshotExport.ReadAsync(repository, async export =>
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            return export.Snapshot;
        }, cancellation.Token)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "after", null, NodeValue.FromObject(42))]).ConfigureAwait(true)).Outcome);
    }

    /// <summary>旧writerの未分類行を本文で選別し、過去のローカル値を共有しません。</summary>
    /// <returns>旧APIと同じ共有集合です。</returns>
    [Fact]
    public async Task UnclassifiedPrivateReceiptsStayPrivateAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        Guid shared = Guid.NewGuid();
        await repository.ApplyBatchAsync(shared, [new("workflows", "shared", null, NodeValue.FromObject(42))]).ConfigureAwait(true);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("peer:fixture", "private", null, NodeValue.FromObject(7))]).ConfigureAwait(true);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("peer:fixture", "mixed", null, NodeValue.FromObject(1)), new("workflows", "mixed", null, NodeValue.FromObject(2))]).ConfigureAwait(true);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE records SET scope=NULL WHERE kind='receipt'";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        Guid id = await SnapshotExport.ReadAsync(repository, export => Task.FromResult(Assert.Single(export.Receipts).Read().OperationId)).ConfigureAwait(true);
        Assert.Equal(shared, id);
    }

    /// <summary>指紋がキャッシュ済みでも、実際に送る本文の破損は書出しを完了させません。</summary>
    /// <returns>内容ハッシュを変更しないDB破損の検出です。</returns>
    [Fact]
    public async Task CachedInventoryDoesNotHideCorruptionInTransferredBytesAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "shared", null, NodeValue.FromObject(42))]).ConfigureAwait(true);
        _ = await SnapshotExport.ReadAsync(repository, export => Task.FromResult(SyncInventory.Describe(export))).ConfigureAwait(true);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE records SET payload=replace(payload,'42','43') WHERE kind='receipt'";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotExport.ReadAsync(repository, export =>
        {
            using MemoryStream output = new();
            return Task.FromResult(SyncSnapshotWriter.WriteFull(output, export, true, CancellationToken.None));
        })).ConfigureAwait(true);
    }
}
