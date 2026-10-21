using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>日時到来・取消競合・再起動後の非再実行を確認します。</summary>
public sealed class UpdateScheduleTests
{
    /// <summary>予約変更は保存版で競合し、同じ変更IDの再送は元結果だけを返します。</summary>
    /// <returns>登録・変更・元ID照会の確認です。</returns>
    [Fact]
    public async Task ChangesCompareVersionsAndReplayOriginalResultAsync()
    {
        using UpdateTestScope scope = new();
        ScheduleLauncher launcher = new();
        UpdateScheduleStore store = new(new JsonFileRepository(Path.Combine(scope.Root, "state.json")), "owner", launcher);
        UpdateScheduleRequest request = Request();
        UpdateScheduleResult first = await store.SaveAsync(request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, first.Outcome);
        Assert.Equal(first.View!.Version, (await store.OperationAsync(request.OperationId).ConfigureAwait(true))!.View!.Version);
        Assert.Equal(first.View!.Version, (await store.SaveAsync(request).ConfigureAwait(true)).View!.Version);
        Assert.Equal(1, launcher.Validations);
        Assert.Equal(EOutcome.NotExecuted, (await store.SaveAsync(request with { Plan = request.Plan with { MaximumDelaySeconds = 1 } }).ConfigureAwait(true)).Outcome);
        UpdateScheduleResult changed = await store.SaveAsync(request with { OperationId = Guid.NewGuid(), ExpectedVersion = first.View.Version, Plan = request.Plan with { MaximumDelaySeconds = 120 } }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, changed.Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await store.CancelAsync(new(Guid.NewGuid(), request.Plan.Update.OperationId, first.View.Version)).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await store.CancelAsync(new(Guid.NewGuid(), request.Plan.Update.OperationId, changed.View!.Version)).ConfigureAwait(true)).Outcome);
        await new UpdateScheduler(store, launcher, () => true, new ScheduleClock(request.Plan.StartAt)).TickAsync().ConfigureAwait(true);
        Assert.Equal(0, launcher.Starts);
    }

    /// <summary>再起動しても保存予約を復元し、猶予内に一回開始します。</summary>
    /// <returns>永続復元から元更新の完了照会までの確認です。</returns>
    [Fact]
    public async Task RestartStartsPendingScheduleOnceAndObservesCompletionAsync()
    {
        using UpdateTestScope scope = new();
        string path = Path.Combine(scope.Root, "state.json");
        ScheduleLauncher launcher = new();
        UpdateScheduleStore store = new(new JsonFileRepository(path), "owner", launcher);
        UpdateScheduleRequest request = Request();
        Assert.Equal(EOutcome.Succeeded, (await store.SaveAsync(request).ConfigureAwait(true)).Outcome);
        ScheduleClock clock = new(request.Plan.StartAt.AddSeconds(-1));
        store = new(new JsonFileRepository(path), "owner", launcher);
        UpdateScheduler scheduler = new(store, launcher, () => true, clock);
        await scheduler.TickAsync().ConfigureAwait(true);
        Assert.Equal(0, launcher.Starts);
        clock.Now = request.Plan.StartAt.AddSeconds(5);
        await scheduler.TickAsync().ConfigureAwait(true);
        Assert.Equal(1, launcher.Starts);
        launcher.Result = new(request.Plan.Update, EOutcome.Succeeded, true, []);
        await scheduler.TickAsync().ConfigureAwait(true);
        Assert.Equal(EUpdateScheduleState.Completed, (await store.ReadAsync(request.Plan.Update.OperationId).ConfigureAwait(true))!.Schedule.State);
        await new UpdateScheduler(store, launcher, () => true, clock).TickAsync().ConfigureAwait(true);
        Assert.Equal(1, launcher.Starts);
    }

    /// <summary>開始時に応答を失った更新は、新しい監視でも起動し直しません。</summary>
    /// <returns>不明状態の元ID照会です。</returns>
    [Fact]
    public async Task LostStartResponseDoesNotReplayAfterRestartAsync()
    {
        using UpdateTestScope scope = new();
        ScheduleLauncher launcher = new() { FailAfterStart = true };
        UpdateScheduleStore store = new(new JsonFileRepository(Path.Combine(scope.Root, "state.json")), "owner", launcher);
        UpdateScheduleRequest request = Request();
        await store.SaveAsync(request).ConfigureAwait(true);
        ScheduleClock clock = new(request.Plan.StartAt);
        await new UpdateScheduler(store, launcher, () => true, clock).TickAsync().ConfigureAwait(true);
        Assert.Equal(EUpdateScheduleState.Unconfirmed, (await store.ReadAsync(request.Plan.Update.OperationId).ConfigureAwait(true))!.Schedule.State);
        await new UpdateScheduler(store, launcher, () => true, clock).TickAsync().ConfigureAwait(true);
        Assert.Equal(1, launcher.Starts);
        Assert.Equal(EOutcome.Unknown, (await store.ReadAsync(request.Plan.Update.OperationId).ConfigureAwait(true))!.Schedule.Outcome);
    }

    /// <summary>起点の保留は猶予まで待ち、期限切れの予約を後から実行しません。</summary>
    /// <returns>遅延と未実行確定です。</returns>
    [Fact]
    public async Task UnavailableOwnerExpiresWithoutStartingAsync()
    {
        using UpdateTestScope scope = new();
        ScheduleLauncher launcher = new();
        UpdateScheduleStore store = new(new JsonFileRepository(Path.Combine(scope.Root, "state.json")), "owner", launcher);
        UpdateScheduleRequest request = Request();
        await store.SaveAsync(request).ConfigureAwait(true);
        ScheduleClock clock = new(request.Plan.StartAt);
        await new UpdateScheduler(store, launcher, () => false, clock).TickAsync().ConfigureAwait(true);
        Assert.Equal(EUpdateScheduleState.Waiting, (await store.ReadAsync(request.Plan.Update.OperationId).ConfigureAwait(true))!.Schedule.State);
        clock.Now = request.Plan.StartAt.AddSeconds(request.Plan.MaximumDelaySeconds + 1);
        await new UpdateScheduler(store, launcher, () => true, clock).TickAsync().ConfigureAwait(true);
        Assert.Equal(EUpdateScheduleState.Expired, (await store.ReadAsync(request.Plan.Update.OperationId).ConfigureAwait(true))!.Schedule.State);
        Assert.Equal(0, launcher.Starts);
    }

    /// <summary>一回の予約を作成します。</summary>
    /// <returns>ファイル配置はfake launcherが検証する固定入力です。</returns>
    private static UpdateScheduleRequest Request()
    {
        ReleaseManifest release = new("new", "win-x64", "app.exe", [], ["old"]);
        ClusterUpdateRequest update = new(Guid.NewGuid(), release, Path.GetTempPath(), ["target"], EUpdateFailurePolicy.KeepCompatibleMixed);
        return new(Guid.NewGuid(), new(DateTimeOffset.UtcNow.AddMinutes(1), 60, update));
    }

    /// <summary>壁時計だけを試験が進めます。</summary>
    private sealed class ScheduleClock(DateTimeOffset now) : TimeProvider
    {
        /// <summary>試験で確認する現在時刻です。</summary>
        public DateTimeOffset Now
        {
            get; set;
        } = now;

        /// <summary>固定した共通時刻です。</summary>
        /// <returns>現在時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>起動回数と元結果を観測できるUpdater境界です。</summary>
    private sealed class ScheduleLauncher : IUpdateScheduleLauncher
    {
        /// <summary>検証呼出し数です。</summary>
        internal int Validations
        {
            get; private set;
        }

        /// <summary>副作用開始を試みた回数です。</summary>
        internal int Starts
        {
            get; private set;
        }

        /// <summary>開始後の応答喪失を模擬します。</summary>
        internal bool FailAfterStart
        {
            get; init;
        }

        /// <summary>元IDで確認できる結果です。</summary>
        internal ClusterUpdateStatus? Result
        {
            get; set;
        }

        /// <summary>固定対象を検証します。</summary>
        /// <returns>実体スナップショットです。</returns>
        public Task<IReadOnlyDictionary<string, Guid>> ValidateAsync(ClusterUpdateRequest request, CancellationToken cancellationToken = default)
        {
            Validations++;
            return Task.FromResult<IReadOnlyDictionary<string, Guid>>(new Dictionary<string, Guid>(StringComparer.Ordinal) { ["target"] = Guid.NewGuid() });
        }

        /// <summary>起動済みかどうかを記録します。</summary>
        /// <returns>受付だけを示す結果です。</returns>
        public Task<ClusterUpdateStatus> StartScheduledAsync(ClusterUpdateRequest request, IReadOnlyDictionary<string, Guid> instances, CancellationToken cancellationToken = default)
        {
            Starts++;
            if (FailAfterStart)
            {
                throw new IOException("lost after dispatch");
            }
            return Task.FromResult(new ClusterUpdateStatus(request, EOutcome.Unknown, false, []));
        }

        /// <summary>再実行せず既知結果だけを返します。</summary>
        /// <returns>確認できた元結果です。</returns>
        public Task<ClusterUpdateStatus?> ReadAsync(Guid operationId, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }
}
