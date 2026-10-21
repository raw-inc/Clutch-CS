using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>単一SQLiteの同期は、所有先を含む一取引で復旧できることを検証します。</summary>
public sealed class AtomicRoutingImportTests
{
    /// <summary>所有先の書込みで実DB例外が起きても、値・履歴・所有先の部分公開を防ぎます。</summary>
    /// <returns>失敗時の全ロールバックと、障害除去後の同じ内容の取込みです。</returns>
    [Fact]
    public async Task OwnerWriteFailureRollsBackValuesAndReceiptsAsync()
    {
        using MediaTestFiles sourceFiles = new(), targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new()).ConfigureAwait(true);
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new()).ConfigureAwait(true);
        Guid operation = Guid.NewGuid();
        Assert.Equal(EOutcome.Succeeded, (await source.ApplyBatchAsync(operation, [new("workflows", "fixture", null, NodeValue.FromObject(42))]).ConfigureAwait(true)).Outcome);
        RepositorySnapshot snapshot = await source.ExportAsync().ConfigureAwait(true);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(targetFiles.Root, "state.db"), Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand failure = connection.CreateCommand();
        failure.CommandText = "CREATE TRIGGER reject_owner BEFORE INSERT ON records WHEN NEW.kind='entry' AND NEW.scope='peer:persistence-routing' BEGIN SELECT RAISE(ABORT,'fixture owner failure'); END";
        await failure.ExecuteNonQueryAsync().ConfigureAwait(true);
        await Assert.ThrowsAsync<SqliteException>(() => target.ImportAsync(snapshot).AsTask()).ConfigureAwait(true);
        Assert.Null(await target.ReadAsync("workflows", "fixture").ConfigureAwait(true));
        Assert.Null(await target.ReadReceiptAsync(operation).ConfigureAwait(true));
        Assert.Null(await target.ReadAsync("peer:persistence-routing", "owner:" + operation.ToString("N")).ConfigureAwait(true));
        failure.CommandText = "DROP TRIGGER reject_owner";
        await failure.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.True(await target.ImportAsync(snapshot).ConfigureAwait(true));
        IDurableRepository reopened = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new()).ConfigureAwait(true);
        Assert.Equal(42, (await reopened.ReadAsync("workflows", "fixture").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal(EOutcome.Succeeded, (await reopened.ReadReceiptAsync(operation).ConfigureAwait(true))!.Outcome);
        Assert.Single((await reopened.ExportAsync().ConfigureAwait(true)).OperationOwners!);
    }

    /// <summary>現在値の祖先索引が揃った再同期では、同じ大きな過去本文を復元・再保存しません。</summary>
    /// <returns>既存内容の維持と、小さい一時割当での再取込みです。</returns>
    [Fact]
    public async Task RepeatedImportReusesVerifiedReceiptValuesAsync()
    {
        using MediaTestFiles sourceFiles = new(), targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new()).ConfigureAwait(true);
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new()).ConfigureAwait(true);
        string? version = null;
        for (int index = 0; index < 64; index++)
        {
            IReadOnlyList<DurableMutation> changes = await source.StampAsync([new("file:reservations", "active", version, NodeValue.FromObject(new string('x', 65536)))]).ConfigureAwait(true);
            DurableReceipt receipt = await source.ApplyBatchAsync(Guid.NewGuid(), changes).ConfigureAwait(true);
            version = Assert.Single(receipt.Entries).Version;
        }
        RepositorySnapshot snapshot = await source.ExportAsync().ConfigureAwait(true);
        Assert.True(await target.ImportAsync(snapshot).ConfigureAwait(true));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(await target.ImportAsync(snapshot).ConfigureAwait(true));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 8388608);
        Assert.Equal(64, (await target.ExportAsync().ConfigureAwait(true)).Receipts.Count);
        Assert.Equal(await source.GetSharedVersionAsync().ConfigureAwait(true), await target.GetSharedVersionAsync().ConfigureAwait(true));
    }
}
