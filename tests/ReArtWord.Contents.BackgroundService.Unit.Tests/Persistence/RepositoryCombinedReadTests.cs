using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>値と競合の一括照会で、現在値・削除・別接続の変更と破損を見落とさないことを確認します。</summary>
[Collection("Performance")]
public sealed class RepositoryCombinedReadTests(ITestOutputHelper output)
{
    /// <summary>指定キーの有無にかかわらず、同じ用途の全競合を従来どおり返します。</summary>
    /// <returns>用途の分離、競合解消後の再照会と削除履歴の確認です。</returns>
    [Theory]
    [InlineData(EStorageBackend.Json)]
    [InlineData(EStorageBackend.Sqlite)]
    public async Task CombinedReadPreservesScopeConflictsAndDeletionAsync(EStorageBackend backend)
    {
        using MediaTestFiles files = new();
        using RepositoryRuntime first = await RepositoryFactory.CreateRuntimeAsync(Path.Combine(files.Root, "first"), "first", new(backend));
        using RepositoryRuntime second = await RepositoryFactory.CreateRuntimeAsync(Path.Combine(files.Root, "second"), "second", new(backend));
        IDurableRepository repository = first.Repository;
        const string scope = "workflow:read-test";
        (DurableEntry? Entry, IReadOnlyList<DurableEntry> Conflicts) absent = await repository.ReadWithConflictsAsync(scope, "missing");
        Assert.Null(absent.Entry);
        Assert.Empty(absent.Conflicts);
        DurableReceipt seed = await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "one", null, NodeValue.FromObject(1))]);
        Assert.True(await second.Repository.ImportAsync(await repository.ExportAsync()));
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "one", seed.Entries[0].Version, NodeValue.FromObject(2))]);
        await second.Repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "one", seed.Entries[0].Version, NodeValue.FromObject(3))]);
        Assert.False(await repository.ImportAsync(await second.Repository.ExportAsync()));
        (DurableEntry? Entry, IReadOnlyList<DurableEntry> Conflicts) conflict = await repository.ReadWithConflictsAsync(scope, "one");
        Assert.Equal(2, conflict.Entry!.Value!.Json.GetInt32());
        Assert.Equal(2, conflict.Conflicts.Count);
        (DurableEntry? Entry, IReadOnlyList<DurableEntry> Conflicts) missing = await repository.ReadWithConflictsAsync(scope, "missing");
        Assert.Null(missing.Entry);
        Assert.Equal(conflict.Conflicts.Select(entry => entry.Version).Order(StringComparer.Ordinal), missing.Conflicts.Select(entry => entry.Version).Order(StringComparer.Ordinal));
        (DurableEntry? Entry, IReadOnlyList<DurableEntry> Conflicts) unrelated = await repository.ReadWithConflictsAsync("workflow:other", "one");
        Assert.Null(unrelated.Entry);
        Assert.Empty(unrelated.Conflicts);
        DurableReceipt resolved = await repository.ResolveConflictAsync(Guid.NewGuid(), scope, "one", conflict.Conflicts.Select(entry => entry.Version).ToArray(), null);
        Assert.Equal(EOutcome.Succeeded, resolved.Outcome);
        (DurableEntry? Entry, IReadOnlyList<DurableEntry> Conflicts) deleted = await repository.ReadWithConflictsAsync(scope, "one");
        Assert.NotNull(deleted.Entry);
        Assert.Null(deleted.Entry.Value);
        Assert.Empty(deleted.Conflicts);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repository.ReadWithConflictsAsync(scope, "one", cancelled.Token).ConfigureAwait(false));
    }

    /// <summary>再利用中でも別接続の更新を取り込み、チェックサム不一致を拒否します。</summary>
    /// <returns>値をキャッシュして破損や更新を隠さないことです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedReadObservesExternalChangesAndRejectsCorruptionAsync(bool corruptConflict)
    {
        using MediaTestFiles files = new();
        using RepositoryRuntime runtime = await RepositoryFactory.CreateRuntimeAsync(files.Root, "peer", new(EStorageBackend.Sqlite));
        const string scope = "peer:test", key = "value";
        await runtime.Repository.WriteLocalSnapshotAsync(scope, key, NodeValue.FromObject(1));
        Assert.Equal(1, (await runtime.Repository.ReadWithConflictsAsync(scope, key)).Entry!.Value!.Json.GetInt32());
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository other = new(path);
        await other.WriteLocalSnapshotAsync(scope, key, NodeValue.FromObject(2));
        Assert.Equal(2, (await runtime.Repository.ReadWithConflictsAsync(scope, key)).Entry!.Value!.Json.GetInt32());
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE records SET payload='{}' WHERE kind='entry' AND scope=$scope";
        if (corruptConflict)
        {
            command.CommandText = "INSERT INTO records(kind,key,scope,payload,hash) VALUES('conflict',$key,$scope,'{}','invalid')";
            command.Parameters.AddWithValue("$key", DurableRepository.GetAddress(scope, key) + "invalid");
        }
        command.Parameters.AddWithValue("$scope", scope);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await runtime.Repository.ReadWithConflictsAsync(scope, key).ConfigureAwait(false));
    }

    /// <summary>小さい状態の巡回で、同じ確認のために取込み確認と取引準備を繰り返しません。</summary>
    /// <returns>同じ値と競合を確認する場合の割り当て量の比較です。</returns>
    [Fact]
    public async Task CombinedReadAvoidsRepeatedTransactionSetupAsync()
    {
        using MediaTestFiles files = new();
        using RepositoryRuntime runtime = await RepositoryFactory.CreateRuntimeAsync(files.Root, "peer", new(EStorageBackend.Sqlite));
        const string scope = "execution:scheduled-index", key = "active";
        await runtime.Repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, key, null, NodeValue.FromObject(1))]);
        await MeasureAsync(runtime.Repository, scope, key, false);
        await MeasureAsync(runtime.Repository, scope, key, true);
        long separate = await MeasureAsync(runtime.Repository, scope, key, false);
        long combined = await MeasureAsync(runtime.Repository, scope, key, true);
        output.WriteLine($"separate={separate} bytes; combined={combined} bytes; iterations=32");
        Assert.True(combined < separate * 0.8, $"separate={separate}, combined={combined}");
    }

    /// <summary>排他待ちでスレッドが切り替わっても、照会全体の割り当て量を測定します。</summary>
    /// <returns>32回の検証に割り当てたバイト数です。</returns>
    private static async Task<long> MeasureAsync(IDurableRepository repository, string scope, string key, bool combined, CancellationToken cancellationToken = default)
    {
        long start = GC.GetTotalAllocatedBytes(true);
        for (int index = 0; index < 32; index++)
        {
            DurableEntry? entry;
            IReadOnlyList<DurableEntry> conflicts;
            if (combined)
            {
                (entry, conflicts) = await repository.ReadWithConflictsAsync(scope, key, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                conflicts = await repository.GetConflictsAsync(scope, cancellationToken).ConfigureAwait(false);
                entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
            }
            Assert.Equal(1, entry!.Value!.Json.GetInt32());
            Assert.Empty(conflicts);
        }
        return GC.GetTotalAllocatedBytes(true) - start;
    }
}
