using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>保守の時刻基準を、実際の本文削除・新規受付・未完了操作の保護まで確認します。</summary>
public sealed class HistoryMaintenanceClockTests
{
    /// <summary>UTCだけが一か月進んでも削除せず、実経過後は期限を守り、現在値と未完了操作は残します。</summary>
    /// <returns>SQLiteとJSONの実保存先での保持整理です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockJumpDoesNotRetireLiveHistoryAndRealElapsedTimeStillDoesAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Create(files.Root, json);
        ResourceSelectionClock time = new();
        time.Adjust(DateTimeOffset.UtcNow - time.GetUtcNow());
        HistoryRetentionClock clock = new(time);
        DateTimeOffset now = time.GetUtcNow();
        using HistoryArchiveService archive = new(repository);
        await MaintainAsync(archive, clock.Capture()).ConfigureAwait(true);
        Guid old = Guid.CreateVersion7(now), next = Guid.CreateVersion7(now);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(old, [new("workflow:clock-retention", "value", null, NodeValue.FromObject(1))]).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(next, [new("workflow:clock-retention", "value", old.ToString("N"), NodeValue.FromObject(2))]).ConfigureAwait(true)).Outcome);
        time.Adjust(TimeSpan.FromDays(31));
        await MaintainAsync(archive, clock.Capture()).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await repository.ReadReceiptAsync(old).ConfigureAwait(true))!.Outcome);
        time.Advance(TimeSpan.FromDays(5));
        await MaintainAsync(archive, clock.Capture()).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, (await repository.ReadReceiptAsync(old).ConfigureAwait(true))!.Outcome);
        Assert.Equal(2, (await repository.ReadAsync("workflow:clock-retention", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Guid pending = Guid.CreateVersion7(clock.Capture());
        Assert.Equal(EOutcome.Succeeded, (await repository.PrepareAsync(pending, [new("workflow:clock-retention", "pending", null, NodeValue.FromObject(3))]).ConfigureAwait(true)).Outcome);
        time.Advance(TimeSpan.FromDays(5));
        await MaintainAsync(archive, clock.Capture()).ConfigureAwait(true);
        Assert.True(await repository.HasPendingWriteAsync("workflow:clock-retention", "pending").ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await repository.CommitAsync(pending).ConfigureAwait(true)).Outcome);
        Assert.Equal(3, (await repository.ReadAsync("workflow:clock-retention", "pending").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal(EOutcome.Unknown, (await repository.ApplyBatchAsync(old, [new("workflow:clock-retention", "value", null, NodeValue.FromObject(1))]).ConfigureAwait(true)).Outcome);
    }

    /// <summary>遅いPeerから引き継いだ基準で整理すると、そのPeerの新規UUIDv7を削除済み範囲へ含めません。</summary>
    /// <returns>実際に作られた境界と、新規要求の受付結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierPeerReferenceProtectsNewOperationIdsAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Create(files.Root, json);
        ResourceSelectionClock time = new();
        time.Adjust(DateTimeOffset.UtcNow - time.GetUtcNow());
        DateTimeOffset slowNow = time.GetUtcNow();
        time.Adjust(TimeSpan.FromDays(31));
        HistoryRetentionClock clock = new(time);
        clock.Observe(new(Guid.NewGuid(), slowNow, 0));
        using HistoryArchiveService archive = new(repository);
        await MaintainAsync(archive, clock.Capture()).ConfigureAwait(true);
        Guid previous = Guid.CreateVersion7(slowNow);
        await repository.ApplyBatchAsync(previous, [new("workflow:clock-retention", "previous", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        await repository.ApplyBatchAsync(Guid.CreateVersion7(slowNow), [new("workflow:clock-retention", "previous", previous.ToString("N"), NodeValue.FromObject(2))]).ConfigureAwait(true);
        time.Advance(TimeSpan.FromDays(5));
        slowNow += TimeSpan.FromDays(5);
        await MaintainAsync(archive, clock.Capture()).ConfigureAwait(true);
        Guid fresh = Guid.CreateVersion7(slowNow);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(fresh, [new("workflow:clock-retention", "fresh", null, NodeValue.FromObject(7))]).ConfigureAwait(true)).Outcome);
        HistoryRetentionBoundary boundary = (await repository.ReadAsync(RepositoryHistoryGuard.Scope, "boundary").ConfigureAwait(true))!.Value!.Json.Deserialize<HistoryRetentionBoundary>()!;
        Assert.True(boundary.OperationBefore < slowNow);
    }

    /// <summary>既に未来の境界を引き継いでいた場合は追加整理を保留し、境界も現在値も戻しません。</summary>
    /// <returns>保留理由と元境界の保持を確認します。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExistingFutureBoundaryHoldsAdditionalRetirementAsync(bool json, bool routed)
    {
        using MediaTestFiles files = new();
        EStorageBackend backend = json ? EStorageBackend.Json
                                       : EStorageBackend.Sqlite;
        IDurableRepository repository = routed ? await RepositoryFactory.CreateAsync(files.Root, "retention-peer", new(backend)).ConfigureAwait(true)
                                               : Create(files.Root, json);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HistoryRetentionBoundary boundary = new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(), now.AddDays(1), now.AddDays(31));
        await repository.WriteLocalSnapshotAsync(RepositoryHistoryGuard.Scope, "boundary", NodeValue.FromObject(boundary)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:clock-retention", "current", null, NodeValue.FromObject(5))]).ConfigureAwait(true)).Outcome);
        using HistoryArchiveService archive = new(repository);
        Assert.Equal(0, await archive.MaintainAsync(new(), new(1, 3), now).ConfigureAwait(true));
        Assert.Equal("History.RetentionClockUnconfirmed", (await archive.ReadStatusAsync().ConfigureAwait(true)).Problem);
        HistoryRetentionBoundary retained = (await repository.ReadAsync(RepositoryHistoryGuard.Scope, "boundary").ConfigureAwait(true))!.Value!.Json.Deserialize<HistoryRetentionBoundary>()!;
        Assert.Equal(boundary.OperationBefore, retained.OperationBefore);
        Assert.Equal(5, (await repository.ReadAsync("workflow:clock-retention", "current").ConfigureAwait(true))!.Value!.Json.GetInt32());
    }

    /// <summary>別Peerの未来の境界だけでは、まだ確定していない予約や現在値を破棄しません。</summary>
    /// <returns>境界の同期後も元の予約を一度だけ確定できることです。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedBoundaryAloneCannotProvePreparedOperationCompletedAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository target = Create(Path.Combine(files.Root, "target"), json), source = Create(Path.Combine(files.Root, "source"), json);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HistoryRetentionBoundary boundary = new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(), now.AddDays(28), now.AddDays(31));
        await source.WriteLocalSnapshotAsync(RepositoryHistoryGuard.Scope, "boundary", NodeValue.FromObject(boundary)).ConfigureAwait(true);
        await target.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:clock-retention", "current", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        Guid pending = Guid.CreateVersion7(now);
        IReadOnlyList<DurableMutation> writes = await target.StampAsync([new("workflow:clock-retention", "pending", null, NodeValue.FromObject(2))]).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await target.PrepareAsync(pending, writes).ConfigureAwait(true)).Outcome);
        Assert.True(await target.ImportAsync(await source.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
        Assert.True(await target.HasPendingWriteAsync("workflow:clock-retention", "pending").ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await target.CommitAsync(pending).ConfigureAwait(true)).Outcome);
        Assert.Equal(1, (await target.ReadAsync("workflow:clock-retention", "current").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal(2, (await target.ReadAsync("workflow:clock-retention", "pending").ConfigureAwait(true))!.Value!.Json.GetInt32());
    }

    /// <summary>物理保存先を分離します。</summary>
    /// <returns>試験だけが使用する保存先です。</returns>
    private static IDurableRepository Create(string root, bool json) => json ? new JsonFileRepository(Path.Combine(root, "state.json"))
                                                                             : new SqliteRepository(Path.Combine(root, "state.db"));

    /// <summary>既存の有限な保守段階を、同じ確認済み日時で進めます。</summary>
    /// <returns>圧縮と削除の複数段階の完了です。</returns>
    private static async Task MaintainAsync(HistoryArchiveService archive, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < 6; index++)
        {
            await archive.MaintainAsync(new(), new(1, 3), now, cancellationToken).ConfigureAwait(false);
        }
    }
}
