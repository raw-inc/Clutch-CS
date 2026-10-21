using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Nodes.State;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>実保存障害とオフラインローカル変更を強いCASから区別します。</summary>
public sealed class AvailableNodeStateTests
{
    /// <summary>JSON書込み不能中のBは旧永続値を残し、SQLiteの操作記録保存は正常です。</summary>
    /// <returns>実障害と復旧後保存の検証です。</returns>
    [Fact]
    public async Task ActualJsonFailureAdoptsUnsavedWithoutForgingDurability()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.json");
        JsonFileRepository repository = new(path);
        SqliteRepository journal = new(Path.Combine(files.Root, "receipts.db"));
        NodeStateStore strong = new(repository, "a");
        using AvailabilityService service = new("a", Options(new(EAvailabilitySaveFailure.UseUnsaved)), journal, files.Root);
        AvailableNodeStateStore store = new(strong, service);
        string scope = NodeStateScope.Peer("a", "test");
        StateWriteResult original = await strong.CompareExchangeAsync(scope, "key", null, NodeValue.FromObject(42)).ConfigureAwait(true);
        AvailabilityValue before = await store.ReadAvailableAsync(scope, "key").ConfigureAwait(true);
        string blocked = path + ".staging";
        Directory.CreateDirectory(blocked);
        try
        {
            AvailabilityWriteResult result = await store.SetAvailableAsync(scope, "key", NodeValue.FromObject(43), before.Revision, Guid.NewGuid()).ConfigureAwait(true);
            Assert.Equal(EOutcome.Failed, result.PersistenceOutcome);
            Assert.True(result.Applied);
            Assert.True(result.Current!.State.IsUnsaved);
            Assert.Equal(original.Current!.Version, result.Current.State.RecoveryRevision);
            Assert.Equal(42, (await strong.ReadAsync(scope, "key").ConfigureAwait(true))!.Value!.Json.GetInt32());
            Assert.Equal(43, (await store.ReadAvailableAsync(scope, "key").ConfigureAwait(true)).Value!.Json.GetInt32());
            Assert.True(Assert.Single(service.GetStates()).IsUnsaved);
            NodeResult node = await new StateNode("get", store).ExecuteAsync(NodeTestContext.Create("""{"scope":"peer","key":"key"}""") with { PeerId = "a", WorkflowId = "test" }).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, node.Outcome);
            Assert.Equal(43, node.Outputs["value"].Json.GetInt32());
            Assert.True(node.Outputs["availability"].Json.GetProperty(nameof(AvailabilityState.IsUnsaved)).GetBoolean());
            Guid receipt = Guid.NewGuid();
            Assert.Equal(EOutcome.Succeeded, (await journal.ApplyBatchAsync(receipt, [new("receipts:a", "test", null, NodeValue.FromObject(1))]).ConfigureAwait(true)).Outcome);
            Assert.NotNull(await journal.ReadReceiptAsync(receipt).ConfigureAwait(true));
            Exception? failure = await Record.ExceptionAsync(async () => await strong.CompareExchangeAsync(scope, "key", original.Current.Version, NodeValue.FromObject(99)).ConfigureAwait(true)).ConfigureAwait(true);
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        finally
        {
            Directory.Delete(blocked);
        }
        AvailabilityValue current = await store.ReadAvailableAsync(scope, "key").ConfigureAwait(true);
        AvailabilityWriteResult recovered = await store.SetAvailableAsync(scope, "key", NodeValue.FromObject(44), current.Revision, Guid.NewGuid()).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, recovered.PersistenceOutcome);
        Assert.False(recovered.Current!.State.IsUnsaved);
        Assert.Equal(44, (await strong.ReadAsync(scope, "key").ConfigureAwait(true))!.Value!.Json.GetInt32());
    }

    /// <summary>再接続で非競合内部値だけを統合し、競合候補を失いません。</summary>
    /// <returns>ローカル採用と比較統合の検証です。</returns>
    [Fact]
    public async Task OfflineInternalChangesMergeOnlyAgainstConfirmedBase()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        NodeStateStore strong = new(repository, "a");
        using AvailabilityService service = new("a", Options(new(Offline: EAvailabilityOffline.LocalUnconfirmed)), repository, files.Root);
        bool online = true;
        AvailableNodeStateStore store = new(strong, service, () => online);
        string scope = NodeStateScope.Peer("a", "test");
        await strong.CompareExchangeAsync(scope, "key", null, NodeValue.FromObject(1)).ConfigureAwait(true);
        AvailabilityValue before = await store.ReadAvailableAsync(scope, "key").ConfigureAwait(true);
        online = false;
        AvailabilityWriteResult local = await store.SetAvailableAsync(scope, "key", NodeValue.FromObject(2), before.Revision, Guid.NewGuid()).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, local.PersistenceOutcome);
        Assert.True(local.Applied);
        Assert.Equal(1, (await strong.ReadAsync(scope, "key").ConfigureAwait(true))!.Value!.Json.GetInt32());
        online = true;
        await store.ReconcilePendingAsync().ConfigureAwait(true);
        Assert.Equal(2, (await strong.ReadAsync(scope, "key").ConfigureAwait(true))!.Value!.Json.GetInt32());
        before = await store.ReadAvailableAsync(scope, "key").ConfigureAwait(true);
        online = false;
        await store.SetAvailableAsync(scope, "key", NodeValue.FromObject(3), before.Revision, Guid.NewGuid()).ConfigureAwait(true);
        await strong.CompareExchangeAsync(scope, "key", before.Revision, NodeValue.FromObject(4)).ConfigureAwait(true);
        online = true;
        await store.ReconcilePendingAsync().ConfigureAwait(true);
        Assert.Equal("Availability.ReconcileConflict", Assert.Single(service.GetStates()).Reason);
        Assert.Equal(3, service.TryGetCurrent(EStoragePurpose.Runtime, AvailableNodeStateStore.DatasetId(scope, "key"))!.Value!.Json.GetInt32());
        Assert.Equal(4, (await strong.ReadAsync(scope, "key").ConfigureAwait(true))!.Value!.Json.GetInt32());
    }

    /// <summary>Detachedのローカル枝は全画面メタデータへ反映し、別オフライン統合に投入しません。</summary>
    /// <returns>専用枝の所有境界の検証です。</returns>
    [Fact]
    public async Task DetachedMetadataDoesNotCreateOfflineCandidate()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        using AvailabilityService service = new("a", new(), repository, files.Root);
        string id = AvailableNodeStateStore.DatasetId(NodeStateScope.Shared("test"), "key");
        await service.RememberLocalBranchAsync(EStoragePurpose.Runtime, id, NodeValue.FromObject(5), "local-branch").ConfigureAwait(true);
        AvailabilityState state = Assert.Single(service.GetStates());
        Assert.True(state.IsLocalUnconfirmed);
        Assert.Equal("Detached.LocalOnly", state.Reason);
        Assert.Null(state.CandidateRevision);
        AvailableNodeStateStore store = new(new NodeStateStore(repository, "a"), service);
        await store.ReconcilePendingAsync().ConfigureAwait(true);
        Assert.Equal(state, Assert.Single(service.GetStates()));
    }

    /// <summary>Runtime用途だけの明示方針を作成します。</summary>
    /// <returns>他用途の既定を変えない設定です。</returns>
    private static AvailabilityOptions Options(AvailabilityPolicy policy) => new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Runtime] = policy });
}
