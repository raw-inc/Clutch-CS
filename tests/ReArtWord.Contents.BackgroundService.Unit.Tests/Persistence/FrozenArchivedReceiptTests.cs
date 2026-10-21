using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>圧縮済み本文への参照でも、失効・破損を固定した元結果として配送しません。</summary>
public sealed class FrozenArchivedReceiptTests
{
    /// <summary>圧縮先が失効または破損した場合は、準備時の指紋だけで成功にしません。</summary>
    /// <returns>旧形式移行後にも維持する本文検証の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptArchivedBodyCannotBeExportedAsync(bool expire)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using HistoryArchiveService archive = new(repository);
        await archive.MaintainAsync(new(), new(), now);
        DurableReceipt old = await repository.ApplyBatchAsync(Guid.CreateVersion7(now),
            [new("file:reservations", "active", null, NodeValue.FromObject(new Dictionary<Guid, object>()))]);
        await repository.ApplyBatchAsync(Guid.CreateVersion7(now),
            [new("file:reservations", "active", old.Entries[0].Version, NodeValue.FromObject(new { Format = 2 }))]);
        FrozenSnapshotExport frozen = Assert.IsType<FrozenSnapshotExport>(await SnapshotExport.FreezeAsync(repository));
        for (int pass = 0; pass < 8; pass++)
        {
            await archive.MaintainAsync(new(), new(RawDays: 7, TotalDays: 30), now.AddMinutes(11));
        }
        Assert.DoesNotContain((await repository.ExportAsync()).Receipts, receipt => receipt.OperationId == old.OperationId);
        Assert.Equal(old.OperationId, (await frozen.ReadAsync(snapshot => Task.FromResult(snapshot.Receipts.Single(receipt => receipt.OperationId == old.OperationId).Read()))).OperationId);
        if (expire)
        {
            for (int pass = 0; pass < 8; pass++)
            {
                await archive.MaintainAsync(new(), new(RawDays: 7, TotalDays: 30), now.AddDays(32));
            }
            await Assert.ThrowsAsync<SnapshotChangedException>(() => frozen.ReadAsync(snapshot => Task.FromResult(snapshot.Receipts.Select(receipt => receipt.Read()).ToArray())));
        }
        else
        {
            using SqliteConnection connection = new("Pooling=False;Data Source=" + path);
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE history_blocks SET data=zeroblob(length(data))";
            Assert.True(0 < await command.ExecuteNonQueryAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => frozen.ReadAsync(snapshot => Task.FromResult(snapshot.Receipts.Select(receipt => receipt.Read()).ToArray())));
        }
    }
}
