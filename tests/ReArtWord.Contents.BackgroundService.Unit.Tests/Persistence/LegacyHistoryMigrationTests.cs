using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>旧台帳本文の移行が現在予約・再照会・同期・保持期限を変えないことを検証します。</summary>
public sealed class LegacyHistoryMigrationTests
{
    /// <summary>旧形式だけを先に圧縮し、元結果の同一性と現在台帳を両媒体で維持します。</summary>
    /// <returns>再起動・再送・逆向き同期を跨いだ移行の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationPreservesOriginalResultAndDoesNotResurrectRawHistoryAsync(bool json)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state");
        IDurableRepository repository = Create(path, json);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DataRetentionSettings policy = new(RawDays: 7, TotalDays: 30);
        using HistoryArchiveService archive = new(repository);
        await archive.MaintainAsync(new(), policy, now);
        Guid oldId = Guid.CreateVersion7(now);
        DurableMutation[] mutations = [new("file:reservations", "active", null, NodeValue.FromObject(new Dictionary<Guid, object>()))];
        DurableReceipt original = await repository.ApplyBatchAsync(oldId, mutations);
        DurableReceipt current = await repository.ApplyBatchAsync(Guid.CreateVersion7(now),
            [new("file:reservations", "active", original.Entries[0].Version, NodeValue.FromObject(new { Format = 2 }))]);
        DurableReceipt operation = await repository.ApplyBatchAsync(Guid.CreateVersion7(now),
            [new("cluster:operations", oldId.ToString("N"), null, NodeValue.FromObject(original))]);
        RepositorySnapshot before = await repository.ExportAsync();
        for (int pass = 0; pass < 8; pass++)
        {
            await archive.MaintainAsync(new(), policy, now.AddMinutes(11));
        }
        repository = Create(path, json);
        Assert.Equal(RepositoryStateVersion.Hash(await repository.ReadCurrentStateAsync()), await repository.GetSharedVersionAsync());
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(await repository.ReadReceiptAsync(oldId)));
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(await repository.ApplyBatchAsync(oldId, mutations)));
        Assert.Equal(current.Entries[0].Version, (await repository.ReadAsync("file:reservations", "active"))!.Version);
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash((await repository.ReadAsync("cluster:operations", oldId.ToString("N")))!.Value!.Json.Deserialize<DurableReceipt>()));
        Assert.DoesNotContain((await repository.ExportAsync()).Receipts, receipt => receipt.OperationId == oldId);
        Assert.Contains((await repository.ExportAsync()).Receipts, receipt => receipt.OperationId == current.OperationId);
        Assert.True(await repository.ImportAsync(before));
        Assert.DoesNotContain((await repository.ExportAsync()).Receipts, receipt => receipt.OperationId == oldId);
        if (!json)
        {
            using SqliteConnection connection = new("Pooling=False;Data Source=" + path);
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM records WHERE kind='receipt' AND key IN ($original,$wrapper)";
            command.Parameters.AddWithValue("$original", oldId.ToString("N"));
            command.Parameters.AddWithValue("$wrapper", operation.OperationId.ToString("N"));
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        long count = (await archive.ReadStatusAsync()).ArchivedRecords;
        await archive.MaintainAsync(new(), policy, now.AddMinutes(12));
        Assert.Equal(count, (await archive.ReadStatusAsync()).ArchivedRecords);
        for (int pass = 0; pass < 8; pass++)
        {
            await archive.MaintainAsync(new(), policy, now.AddDays(32));
        }
        Assert.Equal(EOutcome.Unknown, (await repository.ReadReceiptAsync(oldId))!.Outcome);
        Assert.Equal(current.Entries[0].Version, (await repository.ReadAsync("file:reservations", "active"))!.Version);
    }

    /// <summary>新形式の確定結果と旧形式の未確認結果を、旧台帳の確定履歴と混同しません。</summary>
    /// <returns>本文移行の対象範囲と未確定行の保持です。</returns>
    [Fact]
    public async Task UnknownResultAndCurrentLedgerRemainUnchangedAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        Guid id = Guid.NewGuid();
        DurableEntry ledger = new("file:reservations", "active", "legacy", NodeValue.FromObject(new Dictionary<Guid, object>()));
        DurableReceipt unknown = new(id, "fp", EOutcome.Unknown, [ledger]);
        Assert.True(await repository.ImportAsync(new([ledger], [unknown])));
        using HistoryArchiveService archive = new(repository);
        DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(11);
        for (int pass = 0; pass < 4; pass++)
        {
            await archive.MaintainAsync(new(), new(), now);
        }
        Assert.Contains((await repository.ExportAsync()).Receipts, receipt => receipt.OperationId == id);
        Assert.Equal(ledger.Version, (await repository.ReadAsync(ledger.Scope, ledger.Key))!.Version);
        Assert.Equal(1, (await archive.ReadStatusAsync()).LegacyRecords);
    }

    /// <summary>同じ契約を持つ二つの永続媒体を選びます。</summary>
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
