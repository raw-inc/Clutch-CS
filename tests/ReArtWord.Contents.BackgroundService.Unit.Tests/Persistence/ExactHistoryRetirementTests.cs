using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>日時を持たない新規IDと、実際に削除したIDを実保存先で区別します。</summary>
public sealed class ExactHistoryRetirementTests
{
    /// <summary>既存の日時境界は維持しつつ、新形式の未使用IDをその境界へ巻き込みません。</summary>
    /// <returns>媒体と用途別振分けを通した移行前後の受付結果です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NewIdentityDoesNotInheritAnExistingClockBoundaryAsync(int backend)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await CreateAsync(files.Root, backend).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HistoryRetentionBoundary legacy = new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(), now.AddDays(-30), now);
        await repository.WriteLocalSnapshotAsync(RepositoryHistoryGuard.Scope, "boundary", NodeValue.FromObject(legacy)).ConfigureAwait(true);
        DurableMutation[] change = [new("workflow:exact-retirement", "current", null, NodeValue.FromObject(42))];
        Guid unconfirmed = Guid.CreateVersion7(now.AddDays(-31)), fresh = new OperationIdSequence().Next();
        Assert.Equal(EOutcome.Unknown, (await repository.ApplyBatchAsync(unconfirmed, change).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(fresh, change).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Failed, (await repository.ApplyBatchAsync(fresh, [change[0] with { Value = NodeValue.FromObject(43) }]).ConfigureAwait(true)).Outcome);
        HistoryRetentionBoundary actual = (await repository.ReadAsync(RepositoryHistoryGuard.Scope, "boundary").ConfigureAwait(true))!.Value!.Json.Deserialize<HistoryRetentionBoundary>()!;
        Assert.Equal(legacy.OperationBefore, actual.OperationBefore);
    }

    /// <summary>順不同の確定・欠番・未完了が混在しても、削除したIDだけを復元先へ引き継ぎます。</summary>
    /// <returns>本文整理、再起動、異なる媒体への同期と再送の確認です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RetirementSurvivesRestartAndTransferWithoutClosingGapsAsync(int backend)
    {
        using MediaTestFiles files = new();
        string sourcePath = Path.Combine(files.Root, "source");
        IDurableRepository repository = await CreateAsync(sourcePath, backend).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OperationIdSequence sequence = new();
        Guid[] ids = Enumerable.Range(0, 8).Select(_ => sequence.Next()).ToArray();
        using (HistoryArchiveService archive = new(repository))
        {
            await MaintainAsync(archive, now).ConfigureAwait(true);
            string? version = null;
            foreach (int index in new[] { 3, 0, 5, 1, 6, 7 })
            {
                DurableReceipt result = await repository.ApplyBatchAsync(ids[index], [new("workflow:exact-retirement", "value", version, NodeValue.FromObject(index))]).ConfigureAwait(true);
                Assert.Equal(EOutcome.Succeeded, result.Outcome);
                version = result.Entries.Single().Version;
            }
            Assert.Equal(EOutcome.Succeeded, (await repository.PrepareAsync(ids[4], [new("workflow:exact-retirement", "pending", null, NodeValue.FromObject(4))]).ConfigureAwait(true)).Outcome);
            await MaintainAsync(archive, now.AddDays(5)).ConfigureAwait(true);
        }
        repository = await CreateAsync(sourcePath, backend).ConfigureAwait(true);
        RepositorySnapshot exported = await repository.ExportAsync().ConfigureAwait(true);
        Assert.NotEmpty(exported.RetiredRanges ?? []);
        Assert.Equal(default, exported.HistoryBoundary!.OperationBefore);
        Assert.Equal(1, exported.HistoryBoundary.RetirementFormat);
        RetiredRangeIndex retired = new();
        foreach (RetiredIdRange range in exported.RetiredRanges!)
        {
            retired.Add(range);
        }
        Assert.True(retired.ContainsOperation(ids[0]));
        Assert.True(retired.ContainsOperation(ids[1]));
        Assert.False(retired.ContainsOperation(ids[2]));
        Assert.False(retired.ContainsOperation(ids[4]));
        Assert.True(await repository.HasPendingWriteAsync("workflow:exact-retirement", "pending").ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await repository.CommitAsync(ids[4]).ConfigureAwait(true)).Outcome);
        IDurableRepository restored = await CreateAsync(Path.Combine(files.Root, "restored"), backend ^ 1).ConfigureAwait(true);
        RepositorySnapshot decoded = JsonSerializer.Deserialize<RepositorySnapshot>(JsonSerializer.Serialize(exported))!;
        Assert.True(await restored.ImportAsync(decoded).ConfigureAwait(true));
        foreach (IDurableRepository current in new[] { repository, restored })
        {
            Assert.Equal(EOutcome.Unknown, (await current.ApplyBatchAsync(ids[0], [new("workflow:exact-retirement", "value", ids[3].ToString("N"), NodeValue.FromObject(0))]).ConfigureAwait(true)).Outcome);
            Assert.Equal(EOutcome.Succeeded, (await current.ApplyBatchAsync(ids[2], [new("workflow:exact-retirement", "late", null, NodeValue.FromObject(2))]).ConfigureAwait(true)).Outcome);
            Assert.Equal(7, (await current.ReadAsync("workflow:exact-retirement", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
        }
    }

    /// <summary>復元先に未取得の本文と削除済みIDが共存しても、背景取得で削除済みIDが復活しません。</summary>
    /// <returns>SQLiteバックアップを別の配置へ復元し、形式6から5へ移行した後の受付結果です。</returns>
    [Fact]
    public async Task RestoringABackupKeepsRetiredIdsThroughoutBackgroundRecoveryAsync()
    {
        using MediaTestFiles files = new();
        string sourcePath = Path.Combine(files.Root, "first");
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourcePath, "source", new()).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OperationIdSequence sequence = new();
        Guid retired = sequence.Next(), gap = sequence.Next(), live = sequence.Next();
        await source.ApplyBatchAsync(retired, [new("workflow:backup", "value", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        await source.ApplyBatchAsync(live, [new("workflow:backup", "value", retired.ToString("N"), NodeValue.FromObject(2))]).ConfigureAwait(true);
        using (HistoryArchiveService archive = new(source))
        {
            await MaintainAsync(archive, now.AddDays(5)).ConfigureAwait(true);
        }
        Guid later = sequence.Next();
        await source.ApplyBatchAsync(later, [new("workflow:backup", "other", null, NodeValue.FromObject(3))]).ConfigureAwait(true);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!;
        RepositorySnapshot snapshot = await outgoing.ReadAsync("source", now.AddDays(6)).ConfigureAwait(true);
        Assert.NotEmpty(snapshot.RetiredRanges!);
        Assert.Contains(snapshot.DeferredHistory!, item => item.OperationId == later);
        string targetPath = Path.Combine(files.Root, "target"), copyPath = Path.Combine(files.Root, "copy");
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetPath, "target", new()).ConfigureAwait(true);
        Assert.True(await target.ImportAsync(snapshot).ConfigureAwait(true));
        Directory.CreateDirectory(copyPath);
        using (SqliteConnection originalDb = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(targetPath, "state.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (SqliteConnection copy = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(copyPath, "state.db"), Pooling = false }.ToString()))
        {
            await originalDb.OpenAsync().ConfigureAwait(true);
            await copy.OpenAsync().ConfigureAwait(true);
            originalDb.BackupDatabase(copy);
        }
        string db = Path.Combine(copyPath, "state.db");
        Assert.Equal(6L, Format(db));
        IDurableRepository restored = await RepositoryFactory.CreateAsync(copyPath, "target", new()).ConfigureAwait(true);
        Assert.Equal("Persistence.HistoryExpired", (await restored.ReadReceiptAsync(retired).ConfigureAwait(true))!.Reason);
        Assert.Equal("Persistence.HistoryPending", (await restored.ReadReceiptAsync(later).ConfigureAwait(true))!.Reason);
        RecoveryRepository incoming = RecoveryRepository.Create(restored)!;
        Assert.Equal(snapshot.DeferredHistory!.Count, await incoming.FillAsync(await outgoing.ReadBodiesAsync(snapshot.DeferredHistory).ConfigureAwait(true)).ConfigureAwait(true));
        Assert.Equal(5L, Format(db));
        Assert.Equal("Persistence.HistoryExpired", (await restored.ReadReceiptAsync(retired).ConfigureAwait(true))!.Reason);
        Assert.Equal(EOutcome.Succeeded, (await restored.ReadReceiptAsync(later).ConfigureAwait(true))!.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await restored.ApplyBatchAsync(gap, [new("workflow:backup", "late", null, NodeValue.FromObject(4))]).ConfigureAwait(true)).Outcome);
        Assert.Equal(2, (await restored.ReadAsync("workflow:backup", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
    }

    /// <summary>復元したDBが公開する互換性の版を確認します。</summary>
    /// <returns>保存形式の版です。</returns>
    private static long Format(string path)
    {
        using SqliteConnection db = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT version FROM repository_identity";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>試験専用の保存先を再び開ける形で作ります。</summary>
    /// <returns>単一媒体または用途別の保存先です。</returns>
    private static async Task<IDurableRepository> CreateAsync(string root, int backend, CancellationToken cancellationToken = default)
    {
        if (backend < 2)
        {
            return backend == 0 ? new JsonFileRepository(Path.Combine(root, "state.json"))
                                : new SqliteRepository(Path.Combine(root, "state.db"));
        }
        EStorageBackend kind = backend == 2 ? EStorageBackend.Json
                                            : EStorageBackend.Sqlite;
        return await RepositoryFactory.CreateAsync(root, "retirement-peer", new(kind), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>各段階の処理上限を維持したまま、同じ日時で保守を進めます。</summary>
    /// <returns>圧縮・削除・回収の完了です。</returns>
    private static async Task MaintainAsync(HistoryArchiveService archive, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < 8; index++)
        {
            await archive.MaintainAsync(new(), new(1, 3), now, cancellationToken).ConfigureAwait(false);
        }
    }
}
