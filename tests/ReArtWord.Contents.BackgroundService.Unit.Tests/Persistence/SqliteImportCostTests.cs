using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>小さい差分の取込みが、無関係な履歴量や同じ圧縮ブロックの読取り回数に比例しないことを確認します。</summary>
public sealed class SqliteImportCostTests
{
    /// <summary>後続版を先に取り込んだ未完了予約も、後着した確定結果から元の祖先を検証して解放します。</summary>
    /// <returns>複数世代を飛び越した履歴照合と、現在値を巻き戻さない予約解放です。</returns>
    [Fact]
    public async Task ReceiptOnlyImportReleasesAnOlderReservationAcrossSeveralVersionsAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository source = new(Path.Combine(files.Root, "source.db")), target = new(Path.Combine(files.Root, "target.db"));
        DurableReceipt initial = await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:pending", "value", null, NodeValue.FromObject(0))]).ConfigureAwait(true);
        Assert.True(await target.ImportAsync(await source.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
        Guid operation = Guid.NewGuid();
        IReadOnlyList<DurableMutation> mutations = await source.StampAsync([new("workflow:pending", "value", initial.Entries[0].Version, NodeValue.FromObject(1))]).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await source.PrepareAsync(operation, mutations).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await target.PrepareAsync(operation, mutations).ConfigureAwait(true)).Outcome);
        DurableReceipt committed = await source.CommitAsync(operation).ConfigureAwait(true);
        DurableReceipt latest = committed;
        for (int value = 2; value <= 4; value++)
        {
            latest = await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:pending", "value", latest.Entries[0].Version, NodeValue.FromObject(value))]).ConfigureAwait(true);
        }
        RepositorySnapshot newer = await source.ExportAsync().ConfigureAwait(true);
        Assert.True(await target.ImportAsync(newer with { Receipts = newer.Receipts.Where(receipt => receipt.OperationId != operation).ToArray() }).ConfigureAwait(true));
        Assert.True(await target.HasPendingWriteAsync("workflow:pending", "value").ConfigureAwait(true));
        Assert.True(await target.ImportAsync(new([], [committed])).ConfigureAwait(true));
        Assert.False(await target.HasPendingWriteAsync("workflow:pending", "value").ConfigureAwait(true));
        Assert.Equal(4, (await target.ReadAsync("workflow:pending", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
    }

    /// <summary>因果情報のある別キーの取込みで、大きい過去本文を復元・再保存しません。</summary>
    /// <returns>低い割当量と、無関係な現在値・操作結果の保持です。</returns>
    [Fact]
    public async Task SmallCausalDeltaDoesNotMaterializeUnrelatedSharedHistoryAsync()
    {
        using MediaTestFiles files = new();
        SqliteRepository target = new(Path.Combine(files.Root, "target.db"));
        SqliteRepository source = new(Path.Combine(files.Root, "source.db"));
        Guid original = Guid.NewGuid();
        DurableReceipt large = await target.ApplyBatchAsync(original, [new("workflow:old", "large", null, NodeValue.FromObject(new string('x', 8388608)))]).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await target.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:old", "large", large.Entries[0].Version, NodeValue.FromObject(1))]).ConfigureAwait(true)).Outcome);
        DurableReceipt change = await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:current", "value", null, NodeValue.FromObject(42))]).ConfigureAwait(true);
        RepositorySnapshot delta = await source.ExportAsync().ConfigureAwait(true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(await target.ImportAsync(delta).ConfigureAwait(true));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 2097152);
        Assert.Equal(42, (await target.ReadAsync("workflow:current", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal(1, (await target.ReadAsync("workflow:old", "large").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal(change.Fingerprint, (await target.ReadReceiptAsync(change.OperationId).ConfigureAwait(true))!.Fingerprint);
        Assert.Equal(8388608, (await target.ReadReceiptAsync(original).ConfigureAwait(true))!.Entries[0].Value!.Json.GetString()!.Length);
    }

    /// <summary>同じ圧縮ブロック内の操作をまとめて照合し、不一致と破損は一致に読み替えません。</summary>
    /// <returns>圧縮前と同じ確定結果、有限の割当量、再読取り時の破損検出です。</returns>
    [Fact]
    public async Task ArchivedMatchesVerifyOneBlockAndRejectChangedOrCorruptReceiptsAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using HistoryArchiveService archive = new(repository);
        await archive.MaintainAsync(new(), new(1, 30), now).ConfigureAwait(true);
        List<DurableReceipt> receipts = [];
        string? revision = null;
        for (int index = 0; index < 64; index++)
        {
            DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(now), [new("workflow:archive", "value", revision, NodeValue.FromObject(new string('x', 8192)))]).ConfigureAwait(true);
            receipts.Add(receipt);
            revision = receipt.Entries[0].Version;
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await archive.MaintainAsync(new(), new(1, 30), now.AddDays(2)).ConfigureAwait(true);
        }
        Assert.True(0 < (await archive.ReadStatusAsync().ConfigureAwait(true)).ArchivedRecords);
        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlySet<Guid> matched = await repository.MatchingReceiptsAsync(receipts).ConfigureAwait(true);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 12582912);
        Assert.Equal(receipts.Select(receipt => receipt.OperationId).Order(), matched.Order());
        Assert.Empty(await repository.MatchingReceiptsAsync([receipts[0] with { Fingerprint = "different" }]).ConfigureAwait(true));
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE history_blocks SET data=zeroblob(length(data))";
        Assert.True(0 < await command.ExecuteNonQueryAsync().ConfigureAwait(true));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.MatchingReceiptsAsync(receipts).ConfigureAwait(true)).ConfigureAwait(true);
    }
}
