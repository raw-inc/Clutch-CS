using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>選別の範囲を絞っても、種類別期間・件数超過・再起動後の期限を取りこぼしません。</summary>
public sealed class HistorySelectionTests
{
    /// <summary>個別の短いRaw期間と日付境界を守り、長期保持側を早く圧縮しません。</summary>
    /// <returns>実際に移動した候補と本文の保持です。</returns>
    [Fact]
    public async Task CategoryCutoffsStillSelectOnlyTheirOwnExpiredRecordsAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DateTimeOffset now = DateTimeOffset.UtcNow.AddDays(2);
        string key = Guid.NewGuid().ToString("N");
        RequestReceipt receipt = new("request", Guid.NewGuid(), now.AddDays(-2), new(EOutcome.Succeeded, NodeValue.FromObject("retained")));
        await RequestReceiptPersistence.WriteAsync(repository, "receipts:p", key, null, NodeValue.FromObject(receipt)).ConfigureAwait(true);
        Guid operation = Guid.NewGuid();
        await repository.ApplyBatchAsync(operation, [new("workflow:test", "current", null, NodeValue.FromObject(42))]).ConfigureAwait(true);
        DataRetentionSettings policy = new(30, 90, new Dictionary<ERetentionCategory, RetentionWindow> { [ERetentionCategory.RequestHistory] = new(1, 30) });
        HistoryRow[] rows = SqliteHistoryArchive.PrepareRetained(path, new(KeepDays: 60), policy, now);
        HistoryRow selected = Assert.Single(rows);
        Assert.Equal("receipts:p", selected.Scope);
        Assert.Equal(DurableRepository.GetAddress("receipts:p", key), selected.Key);
        (byte[] data, int length, string hash) = HistoryBlockCodec.Encode(rows);
        Assert.Equal(1, SqliteHistoryArchive.CommitRetained(path, rows, data, length, hash, now));
        Assert.NotNull(await new SqliteRepository(path).ReadAsync("receipts:p", key).ConfigureAwait(true));
        Assert.Equal(42, (await repository.ReadAsync("workflow:test", "current").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal(EOutcome.Succeeded, (await repository.ReadReceiptAsync(operation).ConfigureAwait(true))!.Outcome);
    }

    /// <summary>期間が未来でも件数超過を適用し、期限前の履歴を削除しません。</summary>
    /// <returns>件数制限と永久保持を含む実保存の結果です。</returns>
    [Theory]
    [InlineData(30)]
    [InlineData(null)]
    public async Task CountOverflowAndFutureDeletionRemainIndependentAsync(int? totalDays)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index < 3; index++)
        {
            RequestReceipt receipt = new("request", Guid.NewGuid(), now.AddHours(-1), new(EOutcome.Succeeded, NodeValue.FromObject(index)));
            await RequestReceiptPersistence.WriteAsync(repository, "receipts:p", Guid.NewGuid().ToString("N"), null, NodeValue.FromObject(receipt)).ConfigureAwait(true);
        }
        DataRetentionSettings policy = new(7, totalDays);
        HistoryRow[] rows = SqliteHistoryArchive.PrepareRetained(path, new(MaximumHotRecords: 1, BatchRecords: 2), policy, now);
        Assert.Equal(2, rows.Length);
        (byte[] data, int length, string hash) = HistoryBlockCodec.Encode(rows);
        Assert.Equal(2, SqliteHistoryArchive.CommitRetained(path, rows, data, length, hash, now));
        Assert.Equal(0, SqliteHistoryRetirement.DeleteOne(path, policy, now));
        Assert.Equal(0, SqliteHistoryHotDeletion.DeleteOne(path, policy, now));
        Assert.Equal(3, (await new SqliteRepository(path).ListAsync("receipts:p").ConfigureAwait(true)).Count);
    }
}
