using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scheduling;

/// <summary>Sourceへの配送直前に停止しても、予定の受付・拒否・取消が保存に残ることを確認します。</summary>
public sealed class ScheduleRetentionTests
{
    /// <summary>結果の保存が確認できなくても、元の実体が確認済みの成否は失いません。</summary>
    /// <returns>保存障害中と回復後の、同じ一件の結果照会。</returns>
    [Fact]
    public async Task CompletedResultRemainsKnownWhilePersistenceIsUnconfirmedAsync()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowFixture execution = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable executionLifetime = execution.ConfigureAwait(true);
        await fixture.StartAsync("a", true).ConfigureAwait(true);
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready)]).ConfigureAwait(true);
        PeerCluster peer = fixture.Peers["a"];
        SqliteRepository repository = fixture.Repositories["a"];
        ScheduleCursorStore store = new(peer, repository);
        using ScheduledInputCoordinator coordinator = new(peer, store.Inputs, execution.Compiler, execution.Engine, execution.Sources);
        RemoteExecutionRequest source = Source(peer);
        ScheduleCursor cursor = await store.ReadAsync(source, "timeline", DateTimeOffset.UnixEpoch).ConfigureAwait(true);
        ScheduleInputSnapshot input = Input(source, cursor.Key);
        Assert.Equal(EOutcome.Succeeded, (await store.AdvanceAsync(source, cursor, input).ConfigureAwait(true)).Outcome);
        DurableEntry index = (await repository.ReadAsync(ScheduledInputStore.IndexScope, ScheduledInputStore.IndexKey).ConfigureAwait(true))!;
        DurableReceipt damaged = await repository.ApplyBatchAsync(Guid.NewGuid(), [new(index.Scope, index.Key, index.Version, NodeValue.FromObject("unreadable index"))]).ConfigureAwait(true);
        ExecutionReport actual = new(input.Id, source.WorkflowId, source.Revision, EOutcome.Succeeded, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, []);
        await execution.Engine.ScheduledResult!(actual, CancellationToken.None).ConfigureAwait(true);
        WorkflowRunStatus pending = (await coordinator.ReadAsync(input.Id).ConfigureAwait(true))!;
        Assert.Equal(EOutcome.Succeeded, pending.Outcome);
        Assert.Equal("schedule.resultUnconfirmed", pending.Reason);
        Assert.NotNull(pending.Report);
        Assert.Equal(1, coordinator.UnconfirmedResults);
        _ = await repository.ApplyBatchAsync(Guid.NewGuid(), [new(index.Scope, index.Key, damaged.Entries[0].Version, index.Value)]).ConfigureAwait(true);
        await coordinator.RefreshAsync().ConfigureAwait(true);
        WorkflowRunStatus confirmed = (await coordinator.ReadAsync(input.Id).ConfigureAwait(true))!;
        Assert.Equal(EOutcome.Succeeded, confirmed.Outcome);
        Assert.Null(confirmed.Reason);
        Assert.Equal(0, coordinator.UnconfirmedResults);
    }

    /// <summary>予約後、まだSourceキューへ渡していない一件も、元Sourceの取消対象になります。</summary>
    /// <returns>共有記録と、再起動後の同じ記録の確認。</returns>
    [Fact]
    public async Task SourceCancellationCoversTheReservedButNotEmittedInputAsync()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync("a", true).ConfigureAwait(true);
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready)]).ConfigureAwait(true);
        PeerCluster peer = fixture.Peers["a"];
        ScheduleCursorStore store = new(peer, fixture.Repositories["a"]);
        using ScheduledInputStore inputs = store.Inputs;
        RemoteExecutionRequest source = Source(peer);
        ScheduleCursor cursor = await store.ReadAsync(source, "timeline", DateTimeOffset.UnixEpoch).ConfigureAwait(true);
        ScheduleInputSnapshot input = Input(source, cursor.Key);
        ScheduleCursor advanced = await store.AdvanceAsync(source, cursor, input).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, advanced.Outcome);
        Assert.Equal(1, advanced.Count);
        Assert.True(await store.CancelAsync(source with { Id = Guid.NewGuid() }).ConfigureAwait(true));
        Assert.False((await inputs.ReadAsync(input.Id, false).ConfigureAwait(true))!.Value.Record.CancellationRequested);
        Assert.True(await store.CancelAsync(source).ConfigureAwait(true));
        ScheduledInputRecord cancelled = (await inputs.ReadAsync(input.Id, false).ConfigureAwait(true))!.Value.Record;
        Assert.True(cancelled.CancellationRequested);
        Assert.False(cancelled.DispatchPermitted);
        Assert.NotNull(cancelled.Input);
        Assert.Null(await inputs.ChangeAsync(input.Id, false, current => current with { CancellationRequested = false }).ConfigureAwait(true));
        await fixture.StopAsync("a").ConfigureAwait(true);
        await fixture.StartAsync("a").ConfigureAwait(true);
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready)]).ConfigureAwait(true);
        ScheduleCursorStore restarted = new(fixture.Peers["a"], fixture.Repositories["a"]);
        using ScheduledInputStore restartedInputs = restarted.Inputs;
        Assert.True((await restartedInputs.ReadAsync(input.Id, false).ConfigureAwait(true))!.Value.Record.CancellationRequested);
    }

    /// <summary>元担当の保存準備が残っても取消の確定範囲を示し、失敗する更新を繰り返しません。</summary>
    /// <returns>予約中の参照、担当交代、予約解除後の保存の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedCompletionDoesNotCauseRetryChurnAfterOwnerLossAsync(bool dispatched)
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowFixture execution = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable executionLifetime = execution.ConfigureAwait(true);
        await fixture.StartAsync("a", true).ConfigureAwait(true);
        await fixture.StartAsync("c").ConfigureAwait(true);
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("c", EPeerStatus.Ready)]).ConfigureAwait(true);
        PeerCluster original = fixture.Peers["a"];
        ScheduleCursorStore sourceStore = new(original, fixture.Repositories["a"]);
        using ScheduledInputStore originalInputs = sourceStore.Inputs;
        RemoteExecutionRequest source = Source(original);
        ScheduleCursor cursor = await sourceStore.ReadAsync(source, "timeline", DateTimeOffset.UnixEpoch).ConfigureAwait(true);
        ScheduleInputSnapshot input = Input(source, cursor.Key);
        Assert.Equal(EOutcome.Succeeded, (await sourceStore.AdvanceAsync(source, cursor, input).ConfigureAwait(true)).Outcome);
        Assert.NotNull(await originalInputs.ChangeAsync(input.Id, false, current => current with { DispatchPermitted = dispatched }).ConfigureAwait(true));
        Assert.True(await sourceStore.CancelAsync(source).ConfigureAwait(true));
        (ScheduledInputRecord record, DurableEntry entry) = (await originalInputs.ReadAsync(input.Id, false).ConfigureAwait(true))!.Value;
        (ScheduledInputIndex _, DurableEntry? index) = await originalInputs.IndexAsync(false).ConfigureAwait(true);
        Guid unfinished = Guid.NewGuid();
        DurableMutation[] pending =
        [
            new(entry.Scope, entry.Key, entry.Version, NodeValue.FromObject(record with { ChangeId = unfinished, Input = null, Outcome = EOutcome.Cancelled })),
            new(index!.Scope, index.Key, index.Version, NodeValue.FromObject(new ScheduledInputIndex(unfinished, record.ConfirmedPeers, []))),
        ];
        foreach (SqliteRepository repository in fixture.Repositories.Values)
        {
            Assert.Equal(EOutcome.Succeeded, (await repository.PrepareAsync(unfinished, pending).ConfigureAwait(true)).Outcome);
        }
        await fixture.StopAsync("a").ConfigureAwait(true);
        await fixture.WaitForStatesAsync("c", [("a", EPeerStatus.Unavailable), ("c", EPeerStatus.Ready)]).ConfigureAwait(true);
        SqliteRepository standby = fixture.Repositories["c"];
        ScheduleCursorStore standbyStore = new(fixture.Peers["c"], standby);
        ScheduledInputStore inputs = standbyStore.Inputs;
        using ScheduledInputCoordinator coordinator = new(fixture.Peers["c"], inputs, execution.Compiler, execution.Engine, execution.Sources);
        int operations = (await standby.ListAsync("cluster:operations").ConfigureAwait(true)).Count;
        for (int refresh = 0; refresh < 3; refresh++)
        {
            await coordinator.RefreshAsync().ConfigureAwait(true);
        }
        Assert.Equal(operations, (await standby.ListAsync("cluster:operations").ConfigureAwait(true)).Count);
        Assert.Null(await standby.ReadReceiptAsync(unfinished).ConfigureAwait(true));
        Assert.Null((await inputs.ReadAsync(input.Id, false).ConfigureAwait(true))!.Value.Record.Outcome);
        WorkflowRunStatus view = (await coordinator.ReadAsync(input.Id).ConfigureAwait(true))!;
        EOutcome expected = EOutcome.Cancelled;
        if (dispatched)
        {
            expected = EOutcome.Unknown;
        }
        Assert.Equal(expected, view.Outcome);
        Assert.False(view.Running);
        if (!dispatched)
        {
            Assert.Equal("schedule.resultUnconfirmed", view.Reason);
        }
        foreach (SqliteRepository repository in fixture.Repositories.Values)
        {
            _ = await repository.AbortPreparedAsync(unfinished).ConfigureAwait(true);
        }
        await coordinator.RefreshAsync().ConfigureAwait(true);
        Assert.Equal(expected, (await inputs.ReadAsync(input.Id, false).ConfigureAwait(true))!.Value.Record.Outcome);
        Assert.Empty((await inputs.IndexAsync(false).ConfigureAwait(true)).Index.Entries);
    }

    /// <summary>保持枠が満杯でも拒否した一回は記録し、同じ時間指定の未受付へ戻しません。</summary>
    /// <returns>保持上限、拒否理由、再読込した予定位置の確認。</returns>
    [Fact]
    public async Task CapacityRejectionAdvancesTheOriginalCursorAsync()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync("a", true).ConfigureAwait(true);
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready)]).ConfigureAwait(true);
        PeerCluster peer = fixture.Peers["a"];
        ScheduleCursorStore store = new(peer, fixture.Repositories["a"]);
        using ScheduledInputStore inputs = store.Inputs;
        RemoteExecutionRequest source = Source(peer);
        ScheduleCursor cursor = await store.ReadAsync(source, "timeline", DateTimeOffset.UnixEpoch).ConfigureAwait(true);
        Guid operation = Guid.NewGuid();
        ScheduledInputIndex index = new(operation, peer.GetPeers(), Enumerable.Range(0, 512)
            .Select(_ => new ScheduledInputIndexEntry(Guid.NewGuid(), "other", 100, "a", peer.LocalInstanceId, false)).ToArray());
        DurableReceipt seeded = await peer.ReplicateAsync(operation, [new(ScheduledInputStore.IndexScope, ScheduledInputStore.IndexKey, null, NodeValue.FromObject(index))]).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, seeded.Outcome);
        ScheduleInputSnapshot input = Input(source, cursor.Key);
        ScheduleCursor refused = await store.AdvanceAsync(source, cursor, input).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, refused.Outcome);
        Assert.Equal("schedule.retentionCapacity", refused.Reason);
        Assert.Null(await inputs.ReadAsync(input.Id, false).ConfigureAwait(true));
        ScheduleCursor confirmed = await store.ReadAsync(source, "timeline", DateTimeOffset.UnixEpoch).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, confirmed.Outcome);
        Assert.Equal(1, confirmed.Count);
        Assert.Equal(input.Schedule.ScheduledAt, confirmed.LastScheduledAt);
        Assert.Equal("schedule.retentionCapacity", confirmed.Reason);
        Assert.Equal(512, (await inputs.IndexAsync(false).ConfigureAwait(true)).Index.Entries.Count);
    }

    /// <summary>一つの実Source開始要求を作成します。</summary>
    /// <returns>現在のPeer実体を指定した要求。</returns>
    private static RemoteExecutionRequest Source(PeerCluster peer)
    {
        NodeDefinition node = new("timer", "scheduling.timer", JsonSerializer.SerializeToElement(new { mode = "once", at = DateTimeOffset.UnixEpoch.AddSeconds(1) }));
        WorkflowDefinition workflow = new("w", "timer", [node], []);
        return new(Guid.NewGuid(), Guid.NewGuid(), workflow.Id, ContractJson.Hash(workflow), node, new Dictionary<string, NodeValue>(), peer.LocalPeerId,
            peer.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    /// <summary>Sourceで取得済みだが、キューへは未配送の予定入力を作成します。</summary>
    /// <returns>保存に必要な固定済み入力。</returns>
    private static ScheduleInputSnapshot Input(RemoteExecutionRequest source, string key)
    {
        SourceExecutionInfo info = new(source.Id, source.WorkflowId, source.Node.Id, source.TargetPeerId, source.Revision, DateTimeOffset.UnixEpoch,
            new(ESourceState.Listening), PeerInstanceId: source.TargetInstanceId);
        ScheduledOccurrence schedule = new(key, source.Node.Id, source.Id, DateTimeOffset.UnixEpoch.AddSeconds(1), EScheduleMissedPolicy.Delay, TimeSpan.FromSeconds(1));
        return new(Guid.NewGuid(), Guid.NewGuid(), new(source.WorkflowId, "timer", [source.Node], []), source.Revision, info,
            source.Node.Settings, source.Inputs, NodeResult.Success(), "UTC", null, null, null, new Dictionary<string, Runtime.Workflows.Targets.TargetResolution>(), schedule, null);
    }
}
