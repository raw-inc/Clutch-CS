using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>派生指紋で旧writerの変更や本文破損を隠さず、元履歴と同じ寿命にします。</summary>
public sealed class ReceiptFingerprintTests
{
    /// <summary>旧SQLによる本文更新を検出して再計算し、削除後に索引を残しません。</summary>
    /// <returns>更新後の正規化指紋と、明示削除時の寿命です。</returns>
    [Fact]
    public async Task LegacyWritesInvalidateFingerprintWithoutChangingTheirContractAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DurableReceipt shared = await repository.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "one", null, NodeValue.FromObject("日本語"))]);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("peer:local", "one", null, NodeValue.FromObject(1))]);
        Assert.Equal(1, Count(path));
        FrozenSnapshotExport original = Assert.IsType<FrozenSnapshotExport>(await SnapshotExport.FreezeAsync(repository));
        Assert.Equal(ContractJson.Hash(shared), Assert.Single(original.Description.Receipts).Hash());
        DurableReceipt changed = shared with { Reason = "Legacy.Fixture" };
        Replace(path, changed, false);
        Assert.Equal(0, Count(path));
        FrozenSnapshotExport next = Assert.IsType<FrozenSnapshotExport>(await SnapshotExport.FreezeAsync(repository));
        Assert.Equal(ContractJson.Hash(changed), Assert.Single(next.Description.Receipts).Hash());
        Delete(path, shared.OperationId);
        FrozenSnapshotExport deleted = Assert.IsType<FrozenSnapshotExport>(await SnapshotExport.FreezeAsync(repository));
        Assert.Empty(deleted.Description.Receipts);
        Assert.Equal(0, Count(path));
        DurableReceipt fresh = await repository.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "two", null, NodeValue.FromObject(2))]);
        Assert.Equal(1, Count(path));
        Delete(path, fresh.OperationId);
        Assert.Equal(0, Count(path));
    }

    /// <summary>元本文が破損した場合は、過去の指紋があっても配送時の検査を通しません。</summary>
    /// <returns>書出し時の整合性エラーです。</returns>
    [Fact]
    public async Task CorruptPayloadIsRejectedAfterInventoryWasPreparedAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "one", null, NodeValue.FromObject(1))]);
        FrozenSnapshotExport prepared = Assert.IsType<FrozenSnapshotExport>(await SnapshotExport.FreezeAsync(repository));
        Replace(path, receipt with { Reason = "Corrupt.Fixture" }, true);
        Assert.Equal(0, Count(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => prepared.ReadAsync(snapshot => Task.FromResult(Assert.Single(snapshot.Receipts).Read())));
    }

    /// <summary>レビュー済みの旧履歴を少量ずつ補完し、再起動のたびに再レビューへ戻しません。</summary>
    /// <returns>索引の再構築と、元の共有版・履歴本文を維持する確認です。</returns>
    [Fact]
    public async Task ExistingHistoryIsIndexedByBoundedMaintenanceAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "one", null, NodeValue.FromObject(1))]);
        string version = await repository.GetSharedVersionAsync();
        LegacyIndex(path);
        Assert.Equal(0, Count(path));
        SqliteRepository reopened = new(path);
        Assert.Equal(version, await reopened.GetSharedVersionAsync());
        using HistoryArchiveService history = new(reopened);
        for (int pass = 0; pass < 3; pass++)
        {
            await history.MaintainAsync(new(BatchRecords: 1), new DataRetentionSettings(7, 30), DateTimeOffset.UtcNow);
        }
        Assert.Equal(1, Count(path));
        FrozenSnapshotExport current = Assert.IsType<FrozenSnapshotExport>(await SnapshotExport.FreezeAsync(reopened));
        Assert.Equal(ContractJson.Hash(receipt), Assert.Single(current.Description.Receipts).Hash());
        Assert.Equal(version, await reopened.GetSharedVersionAsync());
    }

    /// <summary>補完途中でDBを開き直しても、確定済み指紋を残して未処理だけを補完します。</summary>
    /// <returns>途中再起動の前後で進捗・共有版・履歴が維持される確認です。</returns>
    [Fact]
    public async Task PartialBackfillSurvivesRepositoryRestartAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        for (int index = 0; index < 8; index++)
        {
            await repository.ApplyBatchAsync(Guid.NewGuid(), [new("shared", index.ToString(System.Globalization.CultureInfo.InvariantCulture), null, NodeValue.FromObject(index))]);
        }
        string version = await repository.GetSharedVersionAsync();
        LegacyIndex(path);
        SqliteRepository first = new(path);
        using (HistoryArchiveService history = new(first))
        {
            for (int pass = 0; pass < 5 && Count(path) == 0; pass++)
            {
                await history.MaintainAsync(new(BatchRecords: 1), new DataRetentionSettings(7, 30), DateTimeOffset.UtcNow);
            }
        }
        long completed = Count(path);
        Assert.InRange(completed, 1, 7);
        SqliteRepository restarted = new(path);
        Assert.Equal(version, await restarted.GetSharedVersionAsync());
        Assert.Equal(completed, Count(path));
        using (HistoryArchiveService history = new(restarted))
        {
            for (int pass = 0; pass < 20 && Count(path) < 8; pass++)
            {
                await history.MaintainAsync(new(BatchRecords: 1), new DataRetentionSettings(7, 30), DateTimeOffset.UtcNow);
            }
        }
        Assert.Equal(8, Count(path));
        Assert.Equal(version, await restarted.GetSharedVersionAsync());
        FrozenSnapshotExport export = Assert.IsType<FrozenSnapshotExport>(await SnapshotExport.FreezeAsync(restarted));
        Assert.Equal(8, export.Description.Receipts.Count);
    }

    /// <summary>指紋索引の導入前にレビューだけが完了した旧DB状態を用意します。</summary>
    private static void LegacyIndex(string path)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM receipt_fingerprints;
            DELETE FROM history_classifications WHERE name='receipt-fingerprints-v1';
            UPDATE history_candidates SET reviewed_hash=(SELECT hash FROM records r WHERE r.kind=history_candidates.kind AND r.key=history_candidates.key);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>本文とチェックサムだけを書く旧版相当の保存を行います。</summary>
    private static void Replace(string path, DurableReceipt receipt, bool corrupt)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        string payload = JsonSerializer.Serialize(receipt);
        command.CommandText = "UPDATE records SET payload=$payload,hash=CASE WHEN $corrupt THEN hash ELSE $hash END WHERE kind='receipt' AND key=$id";
        command.Parameters.AddWithValue("$id", receipt.OperationId.ToString("N"));
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$hash", SqlitePayload.Hash(payload));
        command.Parameters.AddWithValue("$corrupt", corrupt);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    /// <summary>元の履歴を削除した後に派生索引が残らないことを確認します。</summary>
    private static void Delete(string path, Guid id)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM records WHERE kind='receipt' AND key=$id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    /// <summary>共有本文にだけ対応する索引の件数を読みます。</summary>
    /// <returns>保持している照合指紋数です。</returns>
    private static long Count(string path)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM receipt_fingerprints";
        return (long)command.ExecuteScalar()!;
    }
}
