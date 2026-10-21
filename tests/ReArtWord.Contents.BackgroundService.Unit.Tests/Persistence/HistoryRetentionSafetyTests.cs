using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>期間差のある操作記録と長期離脱の予約が、新規実行へ戻らないことを確認します。</summary>
public sealed class HistoryRetentionSafetyTests
{
    /// <summary>開始receiptだけが長く残る場合も、削除済み操作のBeginを成功にしません。</summary>
    /// <returns>設定短縮時に元の処理が再実行されない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredOperationCannotBeginThroughAnOlderSuccessfulReceipt(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Create(Path.Combine(files.Root, "operations"), json);
        using HistoryArchiveService archive = new(repository);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ConfigurationOperationJournal journal = new(repository);
        await archive.MaintainAsync(new(), new(0, 90), now).ConfigureAwait(true);
        Guid id = Guid.NewGuid();
        object request = new { value = 12 };
        Assert.True(await journal.BeginAsync("retention-test", id, request).ConfigureAwait(true));
        await archive.MaintainAsync(new(), new(0, 90), now.AddMinutes(20)).ConfigureAwait(true);
        await journal.CompleteAsync("retention-test", id, new ConfigurationPhaseResult(EOutcome.Succeeded)).ConfigureAwait(true);
        DataRetentionSettings shorter = new(0, 30);
        await archive.MaintainAsync(new(), shorter, now.AddMinutes(30)).ConfigureAwait(true);
        for (int index = 0; index < 8; index++)
        {
            await archive.MaintainAsync(new(), shorter, now.AddDays(35)).ConfigureAwait(true);
        }
        Assert.Null(await journal.ReadAsync("retention-test", id).ConfigureAwait(true));
        Assert.False(await journal.BeginAsync("retention-test", id, request).ConfigureAwait(true));
    }

    /// <summary>本文の保持範囲外でも、元予約と確認済み現在版から古いCASの予約を解放できます。</summary>
    /// <returns>予約だけが永久に残って以後の保存を妨げない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredPreparedOperationIsSupersededWithoutReexecuting(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository source = Create(Path.Combine(files.Root, "source"), json);
        IDurableRepository offline = Create(Path.Combine(files.Root, "offline"), json);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using HistoryArchiveService archive = new(source);
        DataRetentionSettings policy = new(1, 3);
        await archive.MaintainAsync(new(), policy, now).ConfigureAwait(true);
        Guid operation = Guid.CreateVersion7(now);
        IReadOnlyList<DurableMutation> changes = await source.StampAsync([new("workflow:test", "state", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        await offline.PrepareAsync(operation, changes).ConfigureAwait(true);
        await source.ApplyBatchAsync(operation, changes).ConfigureAwait(true);
        await source.ApplyBatchAsync(Guid.CreateVersion7(now), [new("workflow:test", "state", operation.ToString("N"), NodeValue.FromObject(2))]).ConfigureAwait(true);
        for (int index = 0; index < 6; index++)
        {
            await archive.MaintainAsync(new(), policy, now.AddDays(5)).ConfigureAwait(true);
        }
        Assert.True(await offline.ImportAsync(await source.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
        Assert.False(await offline.HasPendingWriteAsync("workflow:test", "state").ConfigureAwait(true));
        Assert.Equal(EOutcome.Unknown, (await offline.CommitAsync(operation).ConfigureAwait(true)).Outcome);
        DurableEntry current = (await offline.ReadAsync("workflow:test", "state").ConfigureAwait(true))!;
        Assert.Equal(EOutcome.Succeeded, (await offline.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "state", current.Version, NodeValue.FromObject(3))]).ConfigureAwait(true)).Outcome);
    }

    /// <summary>業務キーが古いUUIDでも、操作の発行時刻と取り違えません。</summary>
    /// <returns>保持境界を進めた後も通常の新規業務値を保存できます。</returns>
    [Fact]
    public async Task BusinessKeyIsNotAnOperationDeadline()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        Guid old = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddYears(-1));
        HistoryRetentionBoundary boundary = new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await repository.WriteLocalSnapshotAsync("peer:repository-history", "boundary", NodeValue.FromObject(boundary)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:inventory", old.ToString("N"), null, NodeValue.FromObject("new value"))]).ConfigureAwait(true)).Outcome);
    }

    /// <summary>二つの物理媒体で同じ契約を検証します。</summary>
    /// <returns>独立した保存先です。</returns>
    private static IDurableRepository Create(string path, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(path + ".json");
        }
        return new SqliteRepository(path + ".db");
    }
}
