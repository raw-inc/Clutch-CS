using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>本文削除後の状態・一回性・祖先判定を実保存先で検証します。</summary>
public sealed class HistoryRetentionTests
{
    /// <summary>圧縮は参照可能、削除後の再送は確認不能となり現在値を戻しません。</summary>
    /// <returns>再起動と別保存先への同期を含む検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredReceiptCannotReplayAndCurrentStateSurvives(bool json)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "history.db");
        IDurableRepository repository = Create(path, json);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using HistoryArchiveService archive = new(repository);
        DataRetentionSettings policy = new(RawDays: 1, TotalDays: 3);
        await archive.MaintainAsync(new(), policy, now).ConfigureAwait(true);
        Guid first = Guid.CreateVersion7(now);
        await repository.ApplyBatchAsync(first, [new("workflow:test", "state", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        DurableEntry current = (await repository.ReadAsync("workflow:test", "state").ConfigureAwait(true))!;
        await repository.ApplyBatchAsync(Guid.CreateVersion7(now), [new("workflow:test", "state", current.Version, NodeValue.FromObject(2))]).ConfigureAwait(true);
        await MaintainAsync(archive, policy, now.AddDays(2)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await repository.ReadReceiptAsync(first).ConfigureAwait(true))!.Outcome);
        Assert.Empty((await repository.ExportAsync().ConfigureAwait(true)).Receipts);
        await MaintainAsync(archive, policy, now.AddDays(5)).ConfigureAwait(true);
        repository = Create(path, json);
        Assert.Equal(EOutcome.Unknown, (await repository.ReadReceiptAsync(first).ConfigureAwait(true))!.Outcome);
        Assert.NotEqual(EOutcome.Succeeded, (await repository.ApplyBatchAsync(first, [new("workflow:test", "state", null, NodeValue.FromObject(1))]).ConfigureAwait(true)).Outcome);
        Assert.Equal(2, (await repository.ReadAsync("workflow:test", "state").ConfigureAwait(true))!.Value!.Json.GetInt32());
        IDurableRepository joined = Create(Path.Combine(files.Root, "joined.db"), json);
        Assert.True(await joined.ImportAsync(await repository.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
        Assert.Equal(2, (await joined.ReadAsync("workflow:test", "state").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.NotEqual(EOutcome.Succeeded, (await joined.ApplyBatchAsync(first, [new("workflow:test", "state", null, NodeValue.FromObject(1))]).ConfigureAwait(true)).Outcome);
    }

    /// <summary>因果情報のなかった旧版も、本文を捨てた後に誤った競合へ変えません。</summary>
    /// <returns>旧祖先だけを持つ長期離脱側を再取込みする確認です。</returns>
    [Fact]
    public async Task LegacyAncestryRemainsAfterPayloadDeletion()
    {
        using MediaTestFiles files = new();
        SqliteRepository original = new(Path.Combine(files.Root, "old.db"));
        Guid first = Guid.NewGuid();
        await original.ApplyBatchAsync(first, [new("workflow:test", "state", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        RepositorySnapshot offline = (await original.ExportAsync().ConfigureAwait(true)) with { CausalEntries = null };
        await original.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "state", first.ToString("N"), NodeValue.FromObject(2))]).ConfigureAwait(true);
        SqliteRepository repository = new(Path.Combine(files.Root, "current.db"));
        await repository.ImportAsync((await original.ExportAsync().ConfigureAwait(true)) with { CausalEntries = null }).ConfigureAwait(true);
        DurableEntry current = (await repository.ReadAsync("workflow:test", "state").ConfigureAwait(true))!;
        await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new("workflow:test", "state", current.Version, NodeValue.FromObject(3))]).ConfigureAwait(true);
        using HistoryArchiveService archive = new(repository);
        await MaintainAsync(archive, new(1, 3), DateTimeOffset.UtcNow.AddDays(5)).ConfigureAwait(true);
        Assert.True(await repository.ImportAsync(offline).ConfigureAwait(true));
        Assert.Equal(3, (await repository.ReadAsync("workflow:test", "state").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Empty(await repository.GetConflictsAsync(string.Empty).ConfigureAwait(true));
        Assert.Equal(EOutcome.Unknown, (await repository.ReadReceiptAsync(first).ConfigureAwait(true))!.Outcome);
    }

    /// <summary>初回基準点の後は確定変更数と経過時間を独立に評価します。</summary>
    /// <returns>再送、無変更、時間条件を含む検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointsUseCommittedEventsOrDirtyElapsedTime(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Create(Path.Combine(files.Root, "events.db"), json);
        using HistoryArchiveService archive = new(repository);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DataRetentionSettings policy = new(7, 30, CheckpointEvents: 3, CheckpointDays: 3);
        await archive.MaintainAsync(new(), policy, now).ConfigureAwait(true);
        string? version = null;
        for (int index = 0; index < 4; index++)
        {
            Guid operation = Guid.CreateVersion7();
            DurableMutation[] mutations = [new("workflow:test", "value", version, NodeValue.FromObject(index))];
            await repository.ApplyBatchAsync(operation, mutations).ConfigureAwait(true);
            await repository.ApplyBatchAsync(operation, mutations).ConfigureAwait(true);
            version = operation.ToString("N");
            await archive.MaintainAsync(new(), policy, now).ConfigureAwait(true);
        }
        Assert.Equal(2, (await archive.ReadCheckpointsAsync().ConfigureAwait(true)).Count);
        await archive.MaintainAsync(new(), policy, now.AddDays(3)).ConfigureAwait(true);
        Assert.Equal(2, (await archive.ReadCheckpointsAsync().ConfigureAwait(true)).Count);
        await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new("workflow:test", "value", version, NodeValue.FromObject(5))]).ConfigureAwait(true);
        await archive.MaintainAsync(new(), policy, now.AddDays(3)).ConfigureAwait(true);
        IReadOnlyList<HistoryCheckpointInfo> checkpoints = await archive.ReadCheckpointsAsync().ConfigureAwait(true);
        Assert.Equal(3, checkpoints.Count);
        Assert.Equal(5, checkpoints[0].Sequence);
    }

    /// <summary>同じ保存契約を二つの物理媒体で確認します。</summary>
    /// <returns>独立した試験保存先です。</returns>
    private static IDurableRepository Create(string path, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(path);
        }
        return new SqliteRepository(path);
    }

    /// <summary>初回索引化も含め、小さい保守単位だけを進めます。</summary>
    /// <returns>複数段階の保守完了です。</returns>
    private static async Task MaintainAsync(HistoryArchiveService archive, DataRetentionSettings policy, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < 4; index++)
        {
            await archive.MaintainAsync(new(BatchRecords: 100), policy, now, cancellationToken).ConfigureAwait(true);
        }
    }
}
