using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>用途割当、旧形式への展開、物理縮小の境界を実SQLiteで検証します。</summary>
public sealed class HistoryArchiveRoutingTests
{
    /// <summary>所有先と操作結果を圧縮しても、現在値・共有版・同一操作の再適用防止を維持します。</summary>
    /// <returns>再作成と全展開を跨いだ元結果の確認です。</returns>
    [Fact]
    public async Task RoutingOwnerAndLocalReceiptRemainQueryableAcrossArchiveAndExpansion()
    {
        using MediaTestFiles files = new();
        PersistenceOptions options = new(EStorageBackend.Sqlite);
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "p", options);
        Guid operation = Guid.NewGuid();
        DurableMutation[] mutations = [new("peer:p:current", "value", null, NodeValue.FromObject(42))];
        DurableReceipt original = await repository.ApplyBatchAsync(operation, mutations);
        Assert.Equal(EOutcome.Succeeded, original.Outcome);
        string version = await repository.GetSharedVersionAsync();
        string snapshot = ContractJson.Hash(await repository.ExportAsync());
        using (HistoryArchiveService archive = new(repository))
        {
            Assert.Equal(2, await archive.ArchiveOnceAsync(new(KeepDays: 0), DateTimeOffset.UtcNow.AddDays(8)));
        }
        SqliteRepository direct = new(Path.Combine(files.Root, "state.db"));
        string ownerScope = "peer:persistence-routing", ownerKey = "owner:" + operation.ToString("N");
        DurableEntry owner = (await direct.ReadAsync(ownerScope, ownerKey))!;
        RoutingOwner? decoded = await LocalJsonSnapshots.ReadAsync<RoutingOwner>(direct, ownerScope, ownerKey);
        Assert.Equal(operation, decoded!.Operation.OperationId);
        Assert.Equal(EOutcome.Succeeded, await LocalJsonSnapshots.WriteAsync(direct, ownerScope, ownerKey, writer => owner.Value!.Json.WriteTo(writer)));
        Assert.Single(await direct.ListAsync(ownerScope), entry => string.Equals(entry.Key, ownerKey, StringComparison.Ordinal));
        IDurableRepository reopened = await RepositoryFactory.CreateAsync(files.Root, "p", options);
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(await reopened.ApplyBatchAsync(operation, mutations)));
        Assert.Equal(version, await reopened.GetSharedVersionAsync());
        Assert.Equal(snapshot, ContractJson.Hash(await reopened.ExportAsync()));
        Assert.Equal(42, (await reopened.ReadAsync("peer:p:current", "value"))!.Value!.Json.GetInt32());
        using (FileStream activeCore = HistoryRuntimeLease.Acquire(files.Root))
        {
            await Assert.ThrowsAnyAsync<IOException>(async () => await HistoryOfflineMaintenance.RunAsync(files.Root, "p", options, true).ConfigureAwait(false));
        }
        HistoryArchiveStatus expanded = await HistoryOfflineMaintenance.RunAsync(files.Root, "p", options, true);
        Assert.Equal(0, expanded.ArchivedRecords);
        Assert.Equal(0, expanded.Blocks);
        using SqliteConnection connection = new("Pooling=False;Data Source=" + Path.Combine(files.Root, "state.db"));
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM repository_identity";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        IDurableRepository legacyFormat = await RepositoryFactory.CreateAsync(files.Root, "p", options);
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(await legacyFormat.ReadReceiptAsync(operation)));
    }

    /// <summary>大量の確定結果を圧縮した後、停止中の縮小で実ファイル容量も減らせます。</summary>
    /// <returns>再起動後も全結果を読める物理縮小です。</returns>
    [Fact]
    public async Task OfflineCompactionReducesFileAndPreservesArchivedResults()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "p", new(EStorageBackend.Sqlite));
        string payload = new('x', 8192);
        List<string> keys = [];
        for (int index = 0; index < 100; index++)
        {
            string key = Guid.NewGuid().ToString("N");
            keys.Add(key);
            await RequestReceiptPersistence.WriteAsync(repository, "receipts:p", key, null,
                NodeValue.FromObject(new RequestReceipt("fp", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), new(EOutcome.Succeeded, NodeValue.FromObject(payload)))));
        }
        long before = new FileInfo(Path.Combine(files.Root, "state.db")).Length;
        using (HistoryArchiveService archive = new(repository))
        {
            int moved = 0;
            for (int attempt = 0; attempt < 10 && moved < 100; attempt++)
            {
                moved += await archive.ArchiveOnceAsync(new(KeepDays: 0), DateTimeOffset.UtcNow.AddDays(8));
            }
            Assert.Equal(100, moved);
        }
        HistoryArchiveStatus compacted = await HistoryOfflineMaintenance.RunAsync(files.Root, "p", new(EStorageBackend.Sqlite), false);
        Assert.Equal(100, compacted.ArchivedRecords);
        Assert.True(compacted.DatabaseBytes < before);
        foreach (string key in keys)
        {
            Assert.NotNull(await repository.ReadAsync("receipts:p", key));
        }
    }

    /// <summary>保存済みブロックの破損を、実行履歴の欠測と取り違えて副作用を再実行しません。</summary>
    /// <returns>圧縮後の破損時に実行回数が増えないことの確認です。</returns>
    [Fact]
    public async Task StoredBlockCorruptionCannotReplayTheOperation()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        ClusterRequest request = new(Guid.NewGuid(), "test", "p", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null);
        int calls = 0;
        Task<ClusterResponse> Handler(ClusterRequest _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(9)));
        }
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        await executor.ExecuteAsync(request, Handler, true, true);
        using HistoryArchiveService archive = new(repository);
        Assert.Equal(1, await archive.ArchiveOnceAsync(new(KeepDays: 0), DateTimeOffset.UtcNow.AddDays(8)));
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE history_blocks SET data=zeroblob(length(data))";
            await command.ExecuteNonQueryAsync();
        }
        RequestExecutor restarted = new(new SqliteRepository(path), "p", TimeProvider.System);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await restarted.ExecuteAsync(request, Handler, true, true).ConfigureAwait(false));
        Assert.Equal(1, calls);
    }
}
