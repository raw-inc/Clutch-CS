using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>圧縮によって操作の再実行や確認済み結果の喪失を起こさないことを検証します。</summary>
public sealed class HistoryArchiveTests
{
    /// <summary>経過日数と件数超過を独立に使用し、再作成後も同じ要求の元結果を返します。</summary>
    /// <returns>二つの閾値と、圧縮後の重複実行防止です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgeOrCountArchivesCompletedRequestsWithoutRepeatingEffects(bool byAge)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        RequestExecutor executor = new(repository, "p", TimeProvider.System);
        ClusterRequest[] requests = Enumerable.Range(0, 3).Select(_ => NewRequest()).ToArray();
        int effects = 0;
        Task<ClusterResponse> Execute(ClusterRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            effects++;
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject("確定結果🚀" + new string('x', 2000))));
        }
        foreach (ClusterRequest request in requests)
        {
            Assert.Equal(EOutcome.Succeeded, (await executor.ExecuteAsync(request, Execute, true, true)).Outcome);
        }
        using HistoryArchiveService archive = new(repository);
        HistoryArchiveOptions options = new(KeepDays: 10000, MaximumHotRecords: 1, BatchRecords: 2);
        if (byAge)
        {
            options = options with { KeepDays = 0, MaximumHotRecords = 100 };
        }
        Assert.Equal(2, await archive.ArchiveOnceAsync(options, DateTimeOffset.UtcNow.AddMinutes(20)));
        HistoryArchiveStatus status = await archive.ReadStatusAsync();
        Assert.Equal(2, status.ArchivedRecords);
        Assert.True(status.CompressedBytes < status.ExpandedBytes);
        RequestExecutor reopened = new(new SqliteRepository(path), "p", TimeProvider.System);
        foreach (ClusterRequest request in requests)
        {
            ClusterResponse result = await reopened.ExecuteAsync(request, Execute, true, true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.StartsWith("確定結果🚀", result.Value!.GetString(), StringComparison.Ordinal);
            Assert.Equal("Execution.RequestMismatch", (await reopened.ExecuteAsync(request with { Payload = NodeValue.FromObject(9) }, Execute, true, true)).Reason);
        }
        Assert.Equal(3, effects);
        Assert.Equal(3, (await repository.ListAsync("receipts:p")).Count);
    }

    /// <summary>未確定、予約中と共有設定を除外し、圧縮計算中に更新された行も移動しません。</summary>
    /// <returns>確定済みの無変更行だけを移す比較確定です。</returns>
    [Fact]
    public async Task ActiveReservedAndChangedRecordsRemainHot()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddDays(-1);
        string active = Guid.NewGuid().ToString("N"), reserved = Guid.NewGuid().ToString("N"), changed = Guid.NewGuid().ToString("N");
        RequestReceipt complete = new("fingerprint", Guid.NewGuid(), expiry, new(EOutcome.Succeeded, NodeValue.FromObject(7)));
        await RequestReceiptPersistence.WriteAsync(repository, "receipts:p", active, null, NodeValue.FromObject(complete with { Response = null }));
        DurableEntry blocked = (await RequestReceiptPersistence.WriteAsync(repository, "receipts:p", reserved, null, NodeValue.FromObject(complete)))!;
        DurableEntry current = (await RequestReceiptPersistence.WriteAsync(repository, "receipts:p", changed, null, NodeValue.FromObject(complete)))!;
        await repository.PrepareAsync(Guid.NewGuid(), [new("receipts:p", reserved, blocked.Version, NodeValue.FromObject(complete))]);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "configuration", null, NodeValue.FromObject("keep"))]);
        HistoryArchiveOptions options = new(KeepDays: 0, MaximumHotRecords: 1);
        HistoryRow[] selected = SqliteHistoryArchive.Prepare(path, options, DateTimeOffset.UtcNow.AddMinutes(20));
        Assert.Single(selected);
        (byte[] data, int length, string hash) = HistoryBlockCodec.Encode(selected);
        await RequestReceiptPersistence.WriteAsync(repository, "receipts:p", changed, current.Version, NodeValue.FromObject(complete with { Response = new(EOutcome.Succeeded, NodeValue.FromObject(8)) }));
        Assert.Equal(0, SqliteHistoryArchive.Commit(path, selected, data, length, hash, DateTimeOffset.UtcNow.AddMinutes(20)));
        using HistoryArchiveService archive = new(repository);
        Assert.Equal(0, (await archive.ReadStatusAsync()).ArchivedRecords);
        Assert.NotNull(await repository.ReadAsync("receipts:p", active));
        Assert.NotNull(await repository.ReadAsync("receipts:p", reserved));
        Assert.Equal("keep", (await repository.ReadAsync("workflows", "configuration"))!.Value!.GetString());
        Assert.Equal(8, (await repository.ReadAsync("receipts:p", changed))!.Value!.Json.Deserialize<RequestReceipt>()!.Response!.Value!.Json.GetInt32());
    }

    /// <summary>破損した圧縮結果で元の通常記録を削除しません。</summary>
    /// <returns>圧縮確認失敗後も元本文を読めることの確認です。</returns>
    [Fact]
    public async Task DamagedCompressedBlockDoesNotDeleteOriginal()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        string id = Guid.NewGuid().ToString("N");
        await RequestReceiptPersistence.WriteAsync(repository, "receipts:p", id, null,
            NodeValue.FromObject(new RequestReceipt("hash", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), new(EOutcome.Succeeded, NodeValue.FromObject(42)))));
        HistoryRow[] rows = SqliteHistoryArchive.Prepare(path, new(KeepDays: 0), DateTimeOffset.UtcNow.AddMinutes(20));
        (byte[] data, int length, string hash) = HistoryBlockCodec.Encode(rows);
        data[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => SqliteHistoryArchive.Commit(path, rows, data, length, hash, DateTimeOffset.UtcNow.AddMinutes(20)));
        Assert.NotNull(await repository.ReadAsync("receipts:p", id));
    }

    /// <summary>副作用が発生し得る期限内の同一要求を作ります。</summary>
    /// <returns>圧縮後にも再送する固定要求です。</returns>
    private static ClusterRequest NewRequest() => new(Guid.NewGuid(), "test", "p", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.Null);
}
