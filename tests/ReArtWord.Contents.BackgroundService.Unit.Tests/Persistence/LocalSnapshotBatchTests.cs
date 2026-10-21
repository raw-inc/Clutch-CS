using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>診断の一括保存が失敗や取消で片側だけを変更しないことを確認します。</summary>
public sealed class LocalSnapshotBatchTests
{
    /// <summary>二行目でSQLiteが失敗しても、先に更新したヘッダーをロールバックします。</summary>
    /// <returns>実際の取引途中の失敗と元データの復元です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageFailureRollsBackEarlierRowsAsync(bool existingDetail)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        LocalSnapshotWrite header = new("peer:a:workflow-results", "1", NodeValue.FromObject("before"));
        LocalSnapshotWrite detail = new("peer:a:workflow-result-details", "1", NodeValue.FromObject("before details"));
        IReadOnlyList<LocalSnapshotWrite> initial = [header];
        if (existingDetail)
        {
            initial = [header, detail];
        }
        await repository.WriteSnapshotsAsync(initial).ConfigureAwait(true);
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_details_insert BEFORE INSERT ON records WHEN NEW.scope='peer:a:workflow-result-details' BEGIN SELECT RAISE(ABORT,'injected storage failure'); END;
                CREATE TRIGGER reject_details_update BEFORE UPDATE ON records WHEN NEW.scope='peer:a:workflow-result-details' BEGIN SELECT RAISE(ABORT,'injected storage failure'); END;
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }
        await Assert.ThrowsAsync<SqliteException>(() => repository.WriteSnapshotsAsync([header with { Value = NodeValue.Null }, detail with { Value = NodeValue.Null }]).AsTask()).ConfigureAwait(true);
        SqliteRepository restarted = new(path);
        Assert.Equal("before", (await restarted.ReadAsync(header.Scope, header.Key).ConfigureAwait(true))!.Value!.GetString());
        DurableEntry? restored = await restarted.ReadAsync(detail.Scope, detail.Key).ConfigureAwait(true);
        if (existingDetail)
        {
            Assert.Equal("before details", restored!.Value!.GetString());
        }
        else
        {
            Assert.Null(restored);
        }
    }

    /// <summary>全入力の検証後に確定し、再起動で同じ組を参照できます。</summary>
    /// <returns>実媒体二種類の失敗境界と共有状態不変の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrCancelledBatchLeavesBothPreviousValuesAsync(bool json)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state");
        IDurableRepository repository = Create(path, json);
        ILocalSnapshotBatches batches = Assert.IsAssignableFrom<ILocalSnapshotBatches>(repository);
        string version = await repository.GetSharedVersionAsync().ConfigureAwait(true);
        LocalSnapshotWrite header = new("peer:a:workflow-results", "1", NodeValue.FromObject("before"));
        LocalSnapshotWrite detail = new("peer:a:workflow-result-details", "1", NodeValue.FromBytes(new byte[] { 1, 2, 3 }));
        Assert.Equal(EOutcome.Succeeded, await batches.WriteSnapshotsAsync([header, detail]).ConfigureAwait(true));
        LocalSnapshotWrite changed = header with { Value = NodeValue.FromObject("after") };
        await Assert.ThrowsAsync<ArgumentException>(() => batches.WriteSnapshotsAsync([changed, detail with { Scope = "workflow:shared" }]).AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => batches.WriteSnapshotsAsync([changed, header]).AsTask()).ConfigureAwait(true);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batches.WriteSnapshotsAsync([changed, detail with { Value = NodeValue.Null }], cancelled.Token).AsTask()).ConfigureAwait(true);
        IDurableRepository restarted = Create(path, json);
        Assert.Equal("before", (await restarted.ReadAsync(header.Scope, header.Key).ConfigureAwait(true))!.Value!.GetString());
        Assert.Equal(detail.Value.PayloadSize(), (await restarted.ReadAsync(detail.Scope, detail.Key).ConfigureAwait(true))!.Value!.PayloadSize());
        Assert.Equal(version, await restarted.GetSharedVersionAsync().ConfigureAwait(true));
        Assert.Empty((await restarted.ExportAsync().ConfigureAwait(true)).Receipts);
    }

    /// <summary>同媒体の履歴は保存し、別媒体を跨ぐ原子保存は一件目から変更しません。</summary>
    /// <returns>用途別routingでの一括保存と開始前拒否です。</returns>
    [Fact]
    public async Task RoutedBatchKeepsBothHistoryPartsOnTheRuntimeBackendAsync()
    {
        using MediaTestFiles files = new();
        PersistenceOptions options = new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend>
        {
            [EStoragePurpose.Runtime] = EStorageBackend.Json,
        });
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", options).ConfigureAwait(true);
        ILocalSnapshotBatches batches = Assert.IsAssignableFrom<ILocalSnapshotBatches>(repository);
        LocalSnapshotWrite header = new("peer:a:workflow-results", "1", NodeValue.FromObject("saved"));
        LocalSnapshotWrite detail = new("peer:a:workflow-result-details", "1", NodeValue.FromObject("details"));
        Assert.Equal(EOutcome.Succeeded, await batches.WriteSnapshotsAsync([header, detail]).ConfigureAwait(true));
        await Assert.ThrowsAsync<ArgumentException>(() => batches.WriteSnapshotsAsync([header with { Value = NodeValue.Null }, new("peer:a:resources", "history", NodeValue.Null)]).AsTask()).ConfigureAwait(true);
        JsonFileRepository runtime = new(Path.Combine(files.Root, "state.json"));
        Assert.Equal("saved", (await runtime.ReadAsync(header.Scope, header.Key).ConfigureAwait(true))!.Value!.GetString());
        Assert.Equal("details", (await runtime.ReadAsync(detail.Scope, detail.Key).ConfigureAwait(true))!.Value!.GetString());
        SqliteRepository other = new(Path.Combine(files.Root, "state.db"));
        Assert.Null(await other.ReadAsync("peer:a:resources", "history").ConfigureAwait(true));
    }

    /// <summary>同じ契約を実ファイルで検証します。</summary>
    /// <returns>試験専用の保存先です。</returns>
    private static IDurableRepository Create(string path, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(path);
        }
        return new SqliteRepository(path);
    }
}
