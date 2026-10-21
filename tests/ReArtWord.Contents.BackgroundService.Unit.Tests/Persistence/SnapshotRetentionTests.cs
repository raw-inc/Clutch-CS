using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>既定ローリング、明示永久保持、現在の復旧基準点の保護を確認します。</summary>
public sealed class SnapshotRetentionTests
{
    /// <summary>一度照会した最新キーも、同じ取引内で更新・削除された場合は再確認します。</summary>
    /// <returns>保持判断が取引開始時の古い状態へ固定されない確認です。</returns>
    [Fact]
    public async Task SnapshotHeadsObserveUpdatesAndDeletesInTheSameTransaction()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string old = await SaveAsync(repository, now.AddDays(-2), false).ConfigureAwait(true);
        string latest = await SaveAsync(repository, now.AddDays(-1), false).ConfigureAwait(true);
        DurableEntry entry = (await repository.ReadAsync("snapshots", old).ConfigureAwait(true))!;
        SnapshotRecord snapshot = entry.Value!.Json.Deserialize<SnapshotRecord>()! with { CapturedAt = now };
        using SqliteSession session = new(path);
        Assert.True(session.IsLatestSnapshot("snapshots", latest));
        Assert.False(session.IsLatestSnapshot("snapshots", old));
        RepositoryDocument changes = new();
        changes.Entries[DurableRepository.GetAddress(entry.Scope, entry.Key)] = entry with { Value = NodeValue.FromObject(snapshot) };
        session.Save(changes);
        Assert.True(session.IsLatestSnapshot("snapshots", old));
        Assert.False(session.IsLatestSnapshot("snapshots", latest));
        using SqliteCommand remove = session.HistoryCommand();
        remove.CommandText = "DELETE FROM records WHERE kind='entry' AND key=$key";
        remove.Parameters.AddWithValue("$key", DurableRepository.GetAddress("snapshots", old));
        await remove.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.True(session.IsLatestSnapshot("snapshots", latest));
        Assert.False(session.IsLatestSnapshot("snapshots", old));
    }

    /// <summary>最新履歴の一括確認でも、同時刻・対象・種類・離脱保存ごとの最新一件を保護します。</summary>
    /// <returns>古い履歴だけが実際に削除される確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotLookupPreservesEachRecoveryBoundary(bool detached)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        string scope = "snapshots";
        if (detached)
        {
            scope = "peer:test:detached-snapshots";
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeValue value = NodeValue.FromObject(new { state = "preserved" });
        SnapshotRecord[] snapshots = [
            new("a", "first", "peer", ESnapshotKind.Configuration, now.AddDays(-8), value, ContractJson.Hash(value)),
            new("b", "first", "peer", ESnapshotKind.Configuration, now.AddDays(-7), value, ContractJson.Hash(value)),
            new("c", "first", "peer", ESnapshotKind.Configuration, now.AddDays(-7), value, ContractJson.Hash(value)),
            new("d", "second", "peer", ESnapshotKind.Configuration, now.AddDays(-9), value, ContractJson.Hash(value)),
            new("e", "first", "peer", ESnapshotKind.Runtime, now.AddDays(-10), value, ContractJson.Hash(value)),
        ];
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), snapshots.Select(snapshot =>
            new DurableMutation(scope, snapshot.Id, null, NodeValue.FromObject(snapshot))).ToArray()).ConfigureAwait(true)).Outcome);
        string before = await repository.GetSharedVersionAsync().ConfigureAwait(true);
        SqliteRepository upgraded = new(path);
        Assert.Equal(before, await upgraded.GetSharedVersionAsync().ConfigureAwait(true));
        using HistoryArchiveService archive = new(upgraded);
        for (int index = 0; index < 5; index++)
        {
            await archive.MaintainAsync(new(), new(1, 3), now).ConfigureAwait(true);
        }
        Assert.Null(await upgraded.ReadAsync(scope, "a").ConfigureAwait(true));
        Assert.Null(await upgraded.ReadAsync(scope, "b").ConfigureAwait(true));
        foreach (string id in new[] { "c", "d", "e" })
        {
            Assert.NotNull(await upgraded.ReadAsync(scope, id).ConfigureAwait(true));
        }
    }

    /// <summary>過去の通常保存だけを消し、再参加後にも必要な基準点を保持します。</summary>
    /// <returns>保存先と再参加先の両方での確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatestAndPermanentSnapshotsSurviveRolling(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository source = Create(Path.Combine(files.Root, "source"), json);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using HistoryArchiveService archive = new(source);
        await archive.MaintainAsync(new(), new(1, 3), now).ConfigureAwait(true);
        string old = await SaveAsync(source, now.AddDays(-9), false).ConfigureAwait(true);
        string pin = await SaveAsync(source, now.AddDays(-8), true).ConfigureAwait(true);
        string latest = await SaveAsync(source, now.AddDays(-7), false).ConfigureAwait(true);
        for (int index = 0; index < 5; index++)
        {
            await archive.MaintainAsync(new(), new(1, 3), now).ConfigureAwait(true);
        }
        Assert.Null(await source.ReadAsync("snapshots", old).ConfigureAwait(true));
        Assert.NotNull(await source.ReadAsync("snapshots", pin).ConfigureAwait(true));
        Assert.NotNull(await source.ReadAsync("snapshots", latest).ConfigureAwait(true));
        IDurableRepository joined = Create(Path.Combine(files.Root, "joined"), json);
        Assert.True(await joined.ImportAsync(await source.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
        Assert.NotNull(await joined.ReadAsync("snapshots", pin).ConfigureAwait(true));
        Assert.NotNull(await joined.ReadAsync("snapshots", latest).ConfigureAwait(true));
        Assert.True(await joined.IsHistoryRetiredAsync("snapshots", old).ConfigureAwait(true));
        Assert.Equal(await source.GetSharedVersionAsync().ConfigureAwait(true), await joined.GetSharedVersionAsync().ConfigureAwait(true));
    }

    /// <summary>実際のスナップショット形式を共有領域へ保存します。</summary>
    /// <returns>固定した保存識別子です。</returns>
    private static async Task<string> SaveAsync(IDurableRepository repository, DateTimeOffset date, bool permanent, CancellationToken cancellationToken = default)
    {
        string id = Guid.NewGuid().ToString("N");
        NodeValue value = NodeValue.FromObject(new { state = id });
        SnapshotRecord snapshot = new(id, "workflow", "peer", ESnapshotKind.Configuration, date, value, ContractJson.Hash(value), Permanent: permanent);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("snapshots", id, null, NodeValue.FromObject(snapshot))], cancellationToken).ConfigureAwait(true)).Outcome);
        return id;
    }

    /// <summary>二種類の原子的保存で同じ保持結果を確認します。</summary>
    /// <returns>試験専用の保存先です。</returns>
    private static IDurableRepository Create(string path, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(path + ".json");
        }
        return new SqliteRepository(path + ".db");
    }
}
