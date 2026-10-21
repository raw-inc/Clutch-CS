using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>再同期のたびに既存履歴の所有情報を再保存しないことを確認します。</summary>
public sealed class RoutingImportCostTests(ITestOutputHelper output)
{
    /// <summary>一括読取りでも、削除済み・現在結果・未保存・取消を単独読取りと同じに扱います。</summary>
    /// <returns>失効した操作を未実行へ戻さず、重複要求と取消を維持する確認です。</returns>
    [Fact]
    public async Task BatchedReceiptReadsPreserveRetirementAndMissingResults()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid retired = Guid.CreateVersion7(now);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(retired, [new("workflow:batch", "value", null, NodeValue.FromObject(1))])).Outcome);
        await repository.ApplyBatchAsync(Guid.CreateVersion7(now), [new("workflow:batch", "value", retired.ToString("N"), NodeValue.FromObject(2))]);
        using (HistoryArchiveService archive = new(repository))
        {
            for (int index = 0; index < 4; index++)
            {
                await archive.MaintainAsync(new(), new DataRetentionSettings(1, 3), now.AddDays(5));
            }
        }
        Guid current = Guid.CreateVersion7(now.AddDays(6)), missing = Guid.CreateVersion7(now.AddDays(6));
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(current, [new("workflow:batch", "fresh", null, NodeValue.FromObject(3))])).Outcome);
        IReadOnlyDictionary<Guid, DurableReceipt> receipts = await repository.ReadReceiptsAsync([current, retired, missing, retired]);
        Assert.Equal(2, receipts.Count);
        Assert.Equal("Persistence.HistoryExpired", receipts[retired].Reason);
        Assert.Equal(EOutcome.Unknown, receipts[retired].Outcome);
        Assert.Equal(EOutcome.Succeeded, receipts[current].Outcome);
        Assert.False(receipts.ContainsKey(missing));
        foreach (Guid id in new[] { current, retired })
        {
            Assert.Equal(ContractJson.Hash(await repository.ReadReceiptAsync(id)), ContractJson.Hash(receipts[id]));
        }
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => repository.ReadReceiptsAsync([current], cancellation.Token).AsTask());
    }

    /// <summary>準備前後で取消指紋が異なる履歴も一括で読み、個別の確定結果を保持します。</summary>
    /// <returns>圧縮された照合不一致候補の割当上限と、双方向同期後の再実行禁止です。</returns>
    [Fact]
    public async Task DifferentArchivedAbortsAreReadOncePerBlock()
    {
        using MediaTestFiles knownFiles = new();
        using MediaTestFiles earlyFiles = new();
        IDurableRepository known = await RepositoryFactory.CreateAsync(knownFiles.Root, "known", new(EStorageBackend.Sqlite));
        IDurableRepository early = await RepositoryFactory.CreateAsync(earlyFiles.Root, "early", new(EStorageBackend.Sqlite));
        List<Guid> operations = [];
        DurableMutation[] mutations = [new("workflow:cancel", "value", null, NodeValue.FromObject(42))];
        for (int index = 0; index < 128; index++)
        {
            Guid id = Guid.NewGuid();
            operations.Add(id);
            Assert.Equal(EOutcome.Succeeded, (await known.PrepareAsync(id, mutations)).Outcome);
            Assert.NotEmpty((await known.AbortPreparedAsync(id)).Fingerprint);
            Assert.Empty((await early.AbortPreparedAsync(id)).Fingerprint);
        }
        RequestReceipt unrelated = new("fp", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(new string('x', 524288))));
        await known.ApplyBatchAsync(Guid.NewGuid(), [new("receipts:known", "unrelated", null, NodeValue.FromObject(unrelated))]);
        RepositorySnapshot snapshot = await early.ExportAsync();
        using (HistoryArchiveService archive = new(known))
        {
            await archive.MaintainAsync(new(BatchRecords: 512), new DataRetentionSettings(0, null), DateTimeOffset.UtcNow.AddDays(8));
            Assert.True(128 <= (await archive.ReadStatusAsync()).ArchivedRecords);
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp(), before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<bool> imported = known.ImportAsync(snapshot);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine("Different abort import allocated {0} bytes in {1:F1} ms.", allocated, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Assert.True(imported.IsCompletedSuccessfully);
        Assert.True(await imported.ConfigureAwait(true));
        Assert.InRange(allocated, 0, 33554432);
        Assert.True(await early.ImportAsync(await known.ExportAsync()));
        foreach (Guid id in operations)
        {
            Assert.NotEmpty((await known.ReadReceiptAsync(id))!.Fingerprint);
            Assert.Empty((await early.ReadReceiptAsync(id))!.Fingerprint);
            Assert.Equal("Persistence.Aborted", (await known.CommitAsync(id)).Reason);
            Assert.Equal("Persistence.Aborted", (await early.CommitAsync(id)).Reason);
        }
        Assert.Null(await known.ReadAsync("workflow:cancel", "value"));
        Assert.Null(await early.ReadAsync("workflow:cancel", "value"));
    }

    /// <summary>保存JSONの空白だけが異なる旧履歴も、同じ確定結果としてまとめて検証します。</summary>
    /// <returns>一件ごとの圧縮展開を避け、元結果と破損検出を維持することです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EquivalentReceiptRepresentationsDoNotRepeatArchiveReads(bool compressed)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "p", new(EStorageBackend.Sqlite));
        for (int index = 0; index < 128; index++)
        {
            Assert.Equal("Persistence.Aborted", (await repository.AbortPreparedAsync(Guid.NewGuid())).Reason);
        }
        RequestReceipt unrelated = new("fp", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(new string('x', 524288))));
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("receipts:p", "unrelated", null, NodeValue.FromObject(unrelated))]);
        RepositorySnapshot snapshot = await repository.ExportAsync();
        string path = Path.Combine(files.Root, "state.db");
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE records SET payload=$payload,hash=$hash WHERE kind='receipt' AND key=$id";
            foreach (DurableReceipt receipt in snapshot.Receipts)
            {
                string payload = "\n" + System.Text.Json.JsonSerializer.Serialize(receipt);
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", receipt.OperationId.ToString("N"));
                command.Parameters.AddWithValue("$payload", payload);
                command.Parameters.AddWithValue("$hash", SqlitePayload.Hash(payload));
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            await transaction.CommitAsync();
        }
        if (compressed)
        {
            using HistoryArchiveService archive = new(repository);
            await archive.MaintainAsync(new(), new DataRetentionSettings(0, null), DateTimeOffset.UtcNow.AddDays(8));
            Assert.True(128 <= (await archive.ReadStatusAsync()).ArchivedRecords);
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp(), before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<bool> imported = repository.ImportAsync(snapshot);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine("Equivalent receipt import allocated {0} bytes in {1:F1} ms.", allocated, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Assert.True(imported.IsCompletedSuccessfully);
        Assert.True(await imported.ConfigureAwait(true));
        Assert.InRange(allocated, 0, 33554432);
        Assert.Equal("Persistence.Aborted", (await repository.CommitAsync(snapshot.Receipts[0].OperationId)).Reason);
        RepositorySnapshot different = snapshot with { Receipts = [snapshot.Receipts[0] with { Reason = "different-result" }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ImportAsync(different).AsTask());
        using SqliteConnection damaged = new("Pooling=False;Data Source=" + path);
        await damaged.OpenAsync();
        using SqliteCommand corrupt = damaged.CreateCommand();
        if (compressed)
        {
            corrupt.CommandText = "UPDATE history_blocks SET data=zeroblob(length(data))";
        }
        else
        {
            corrupt.CommandText = "UPDATE records SET payload='{}' WHERE kind='receipt'";
        }
        await corrupt.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ImportAsync(snapshot).AsTask());
    }

    /// <summary>交互に並ぶ複数ブロックの項目も一度ずつ展開し、順序と破損検出を維持します。</summary>
    /// <returns>非連続読取りの割当上限、重複キー、現在値の優先と破損時の失敗です。</returns>
    [Fact]
    public async Task ArchivedEntriesAreReadInBlockOrder()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db"), scope = "workflow.history:batch";
        SqliteRepository repository = new(path);
        string padding = new('x', 8192);
        DurableMutation[] mutations = Enumerable.Range(0, 128).Select(index => new DurableMutation(scope, index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), null,
            NodeValue.FromObject(new { Revision = index, Padding = padding }))).ToArray();
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), mutations)).Outcome);
        using (HistoryArchiveService archive = new(repository))
        {
            for (int index = 0; index < 8; index++)
            {
                await archive.MaintainAsync(new(BatchRecords: 32), new(0, null), DateTimeOffset.UtcNow.AddDays(8));
            }
            Assert.True(128 <= (await archive.ReadStatusAsync()).ArchivedRecords);
        }
        string[] keys = Enumerable.Range(0, 32).SelectMany(index => Enumerable.Range(0, 4).Select(block => mutations[index + block * 32].Key)).ToArray();
        string[] requested = [.. keys, keys[0], "missing"];
        long started = System.Diagnostics.Stopwatch.GetTimestamp(), before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<DurableEntry> entries = await repository.ReadEntriesAsync(scope, requested).ConfigureAwait(true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine("Batch read allocated {0} bytes in {1:F1} ms.", allocated, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Assert.Equal(keys.Append(keys[0]), entries.Select(entry => entry.Key));
        Assert.InRange(allocated, 0, 20971520);
        DurableEntry updated = (await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, keys[0], entries[0].Version, NodeValue.FromObject("updated"))])).Entries.Single();
        DurableEntry current = Assert.Single(await repository.ReadEntriesAsync(scope, [keys[0]]).ConfigureAwait(true));
        Assert.Equal(updated.Version, current.Version);
        Assert.Equal("updated", current.Value!.GetString());
        using SqliteConnection connection = new("Pooling=False;Data Source=" + path);
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE history_blocks SET data=zeroblob(length(data))";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ReadEntriesAsync(scope, [keys[1]]).AsTask());
    }

    /// <summary>取消結果の補正で、同じ圧縮ブロックを操作件数分だけ再展開しません。</summary>
    /// <returns>取消の再実行防止を維持した再同期の割当上限です。</returns>
    [Fact]
    public async Task ArchivedAbortsReuseTheVerifiedOriginalReceipts()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "p", new(EStorageBackend.Sqlite));
        List<Guid> operations = [];
        for (int index = 0; index < 256; index++)
        {
            Guid operation = Guid.NewGuid();
            operations.Add(operation);
            Assert.Equal("Persistence.Aborted", (await repository.AbortPreparedAsync(operation)).Reason);
        }
        RequestReceipt unrelated = new("fp", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(new string('x', 524288))));
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("receipts:p", "unrelated", null, NodeValue.FromObject(unrelated))]);
        RepositorySnapshot snapshot = await repository.ExportAsync();
        Assert.Equal(256, snapshot.Receipts.Count);
        using (HistoryArchiveService archive = new(repository))
        {
            await archive.MaintainAsync(new(), new DataRetentionSettings(0, null), DateTimeOffset.UtcNow.AddDays(8));
            Assert.True(256 <= (await archive.ReadStatusAsync()).ArchivedRecords);
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<bool> imported = repository.ImportAsync(snapshot);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine("Import allocated {0} bytes in {1:F1} ms.", allocated, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Assert.True(imported.IsCompletedSuccessfully);
        Assert.True(await imported.ConfigureAwait(true));
        Assert.InRange(allocated, 0, 33554432);
        foreach (Guid operation in operations)
        {
            Assert.Equal("Persistence.Aborted", (await repository.CommitAsync(operation)).Reason);
        }
    }

    /// <summary>一致する履歴の版を維持し、不一致の同じ操作IDは取込み前に拒否します。</summary>
    /// <returns>SQLiteとJSONの両方で履歴保存の不必要な更新がないことです。</returns>
    [Theory]
    [InlineData(EStorageBackend.Sqlite)]
    [InlineData(EStorageBackend.Json)]
    public async Task RepeatedImportPreservesOwnerVersionsAndRejectsInconsistentDuplicates(EStorageBackend backend)
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles destinationFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "a", new PersistenceOptions(backend));
        IDurableRepository destination = await RepositoryFactory.CreateAsync(destinationFiles.Root, "b", new PersistenceOptions(backend));
        for (int index = 0; index < 32; index++)
        {
            Assert.Equal(EOutcome.Succeeded, (await source.ApplyBatchAsync(Guid.NewGuid(),
                [new("workflow:retained", index.ToString(System.Globalization.CultureInfo.InvariantCulture), null, NodeValue.FromObject(index))])).Outcome);
        }
        RepositorySnapshot snapshot = await source.ExportAsync();
        Assert.True(await destination.ImportAsync(snapshot));
        IDurableRepository raw = backend switch
        {
            EStorageBackend.Sqlite => new SqliteRepository(Path.Combine(destinationFiles.Root, "state.db")),
            _ => new JsonFileRepository(Path.Combine(destinationFiles.Root, "state.json")),
        };
        Dictionary<string, string> versions = (await raw.ListAsync("peer:persistence-routing")).Where(entry => entry.Key.StartsWith("owner:", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Version);
        Assert.Equal(32, versions.Count);
        Assert.True(await destination.ImportAsync(snapshot));
        foreach (DurableEntry entry in (await raw.ListAsync("peer:persistence-routing")).Where(entry => entry.Key.StartsWith("owner:", StringComparison.Ordinal)))
        {
            Assert.Equal(versions[entry.Key], entry.Version);
        }
        DurableReceipt first = snapshot.Receipts[0];
        RepositorySnapshot invalid = snapshot with { Receipts = [.. snapshot.Receipts, first with { Outcome = EOutcome.Unknown, Reason = "different-result" }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => destination.ImportAsync(invalid).AsTask());
        Assert.Equal(EOutcome.Succeeded, (await destination.ReadReceiptAsync(first.OperationId))!.Outcome);
        Assert.Equal(await source.GetSharedVersionAsync(), await destination.GetSharedVersionAsync());
    }
}
