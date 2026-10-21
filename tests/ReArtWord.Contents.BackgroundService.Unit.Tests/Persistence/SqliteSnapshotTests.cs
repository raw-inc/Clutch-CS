using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>同期でローカル値を複製せず、既存履歴と予約の意味を維持することを検証します。</summary>
public sealed class SqliteSnapshotTests
{
    /// <summary>ローカルの大きな現在値・過去値を、繰返し同期の一時オブジェクトへ読み込みません。</summary>
    /// <returns>割当量の上限とローカル状態の保持です。</returns>
    [Fact]
    public async Task RepeatedSyncDoesNotMaterializePrivatePayloads()
    {
        using MediaTestFiles files = new();
        SqliteRepository store = new(Path.Combine(files.Root, "state.db"));
        Guid localId = Guid.NewGuid();
        await store.ApplyBatchAsync(localId, [new("peer:p:requests", "large", null, NodeValue.FromObject(new string('x', 8388608)))]);
        await store.WriteLocalSnapshotAsync("peer:p:resources", "history", NodeValue.FromObject(new string('y', 8388608)));
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflow", "shared", null, NodeValue.FromObject(42))]);
        RepositorySnapshot expected = await store.ExportAsync();
        Assert.Single(expected.Entries);
        Assert.Single(expected.Receipts);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10; index++)
        {
            RepositorySnapshot snapshot = await store.ExportAsync();
            Assert.True(await store.ImportAsync(snapshot));
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 2097152);
        Assert.Equal(8388608, (await store.ReadAsync("peer:p:requests", "large"))!.Value!.Json.GetString()!.Length);
        Assert.Equal(8388608, (await store.ReadAsync("peer:p:resources", "history"))!.Value!.Json.GetString()!.Length);
        Assert.Equal(8388608, Assert.Single((await store.ReadReceiptAsync(localId))!.Entries).Value!.Json.GetString()!.Length);
    }

    /// <summary>旧writerのNULL分類を読め、分類補完で保存内容やチェックサムを変更しません。</summary>
    /// <returns>旧版との往復互換性です。</returns>
    [Fact]
    public async Task LegacyUnclassifiedReceiptsRemainReadable()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        Guid shared = Guid.NewGuid();
        await store.ApplyBatchAsync(shared, [new("workflow", "shared", null, NodeValue.FromObject("共有"))]);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("peer:p", "private", null, NodeValue.FromObject("局所"))]);
        string before;
        using (SqliteConnection connection = Open(path))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE records SET scope=NULL WHERE kind='receipt'; SELECT group_concat(hash,'|') FROM (SELECT hash FROM records WHERE kind='receipt' ORDER BY key)";
            before = (string)(await command.ExecuteScalarAsync().ConfigureAwait(true))!;
        }
        Assert.Equal(shared, Assert.Single((await store.ExportAsync()).Receipts).OperationId);
        store = new SqliteRepository(path);
        Assert.Equal(shared, Assert.Single((await store.ExportAsync()).Receipts).OperationId);
        using SqliteConnection reopened = Open(path);
        using SqliteCommand check = reopened.CreateCommand();
        check.CommandText = "SELECT group_concat(hash,'|') FROM (SELECT hash FROM records WHERE kind='receipt' ORDER BY key)";
        Assert.Equal(before, await check.ExecuteScalarAsync().ConfigureAwait(true));
    }

    /// <summary>ローカルと共有を同時変更した履歴も、祖先の照合から落としません。</summary>
    /// <returns>古いスナップショットで偽の競合を作らない検証です。</returns>
    [Fact]
    public async Task MixedReceiptHistoryStillRecognizesAncestors()
    {
        using MediaTestFiles files = new();
        SqliteRepository store = new(Path.Combine(files.Root, "state.db"));
        Guid first = Guid.NewGuid();
        await store.ApplyBatchAsync(first, [new("workflow", "x", null, NodeValue.FromObject(0))]);
        RepositorySnapshot old = await store.ExportAsync();
        Guid mixed = Guid.NewGuid();
        await store.ApplyBatchAsync(mixed, [new("workflow", "x", first.ToString("N"), NodeValue.FromObject(1)), new("peer:p", "private", null, NodeValue.FromObject("保持"))]);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflow", "x", mixed.ToString("N"), NodeValue.FromObject(2))]);
        Assert.True(await store.ImportAsync(old));
        Assert.Empty(await store.GetConflictsAsync(string.Empty));
        Assert.Equal(2, (await store.ReadAsync("workflow", "x"))!.Value!.Json.GetInt32());
        Assert.Equal("保持", (await store.ReadAsync("peer:p", "private"))!.Value!.Json.GetString());
        Assert.Equal(2, (await store.ReadReceiptAsync(mixed))!.Entries.Count);
        Assert.DoesNotContain((await store.ExportAsync()).Receipts, receipt => receipt.OperationId == mixed);
    }

    /// <summary>共有スナップショットのIDがローカル履歴と重なっても既存の確定結果を置換しません。</summary>
    /// <returns>取込み前後のローカル操作の同一性です。</returns>
    [Fact]
    public async Task ImportDoesNotOverwriteAnExistingPrivateReceipt()
    {
        using MediaTestFiles files = new();
        SqliteRepository store = new(Path.Combine(files.Root, "state.db"));
        Guid id = Guid.NewGuid();
        DurableReceipt local = await store.ApplyBatchAsync(id, [new("peer:p", "x", null, NodeValue.FromObject("local"))]);
        DurableEntry incoming = new("workflow", "x", id.ToString("N"), NodeValue.FromObject("shared"));
        Assert.True(await store.ImportAsync(new([incoming], [new(id, "different", EOutcome.Succeeded, [incoming])])));
        Assert.Equal(local.Fingerprint, (await store.ReadReceiptAsync(id))!.Fingerprint);
        Assert.Equal("local", (await store.ReadAsync("peer:p", "x"))!.Value!.Json.GetString());
    }

    /// <summary>バックエンドへ渡す前の拒否も共有し、ローカルスコープを含む拒否は共有しません。</summary>
    /// <returns>所有情報を選択取得しても空の確定結果を落とさない検証です。</returns>
    [Fact]
    public async Task RoutingRetainsSharedRejectionsWithoutExportingPrivateOnes()
    {
        using MediaTestFiles files = new();
        PersistenceOptions options = new(Purposes: new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Runtime] = EStorageBackend.Json });
        IDurableRepository store = await RepositoryFactory.CreateAsync(files.Root, "a", options).ConfigureAwait(true);
        Guid shared = Guid.NewGuid();
        DurableReceipt rejected = await store.ApplyBatchAsync(shared, [new("workflows", "a", null, NodeValue.FromObject(1)), new("workflow:state", "a", null, NodeValue.FromObject(2))]);
        Assert.Equal(EOutcome.NotExecuted, rejected.Outcome);
        Guid local = Guid.NewGuid();
        Assert.Equal(EOutcome.NotExecuted, (await store.ApplyBatchAsync(local, [new("peer:a:resources", "a", null, NodeValue.FromObject(3)), new("workflow:state", "b", null, NodeValue.FromObject(4))])).Outcome);
        RepositorySnapshot snapshot = await store.ExportAsync();
        Assert.Equal(shared, Assert.Single(snapshot.Receipts).OperationId);
        Assert.Equal(shared, Assert.Single(snapshot.OperationOwners!).OperationId);
        Assert.DoesNotContain(snapshot.Receipts, receipt => receipt.OperationId == local);
    }

    /// <summary>検証用接続を呼出側のusingへ返します。</summary>
    /// <returns>開いたSQLite接続です。</returns>
    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
