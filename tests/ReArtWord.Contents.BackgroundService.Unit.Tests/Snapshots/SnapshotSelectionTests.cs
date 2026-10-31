using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Nodes.State;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Snapshots;

/// <summary>部分復元と未保存編集の保護範囲を検証します。</summary>
public sealed class SnapshotSelectionTests
{
    /// <summary>設定予約IDはワークフロー保存に使わず、既存衝突でも別対象を暗黙取得しません。</summary>
    /// <returns>予約と曖昧対象の拒否結果です。</returns>
    [Fact]
    public async Task ConfigurationParticipantIdentityCannotBeUsedAsWorkflowIdentity()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Create(repository);
        await cluster.StartAsync();
        WorkflowStore workflows = Store(repository, cluster, new(StringComparer.Ordinal));
        using ClusterConfigurationStore common = new(repository, cluster);
        await common.InitializeAsync(new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)), true);
        SnapshotService snapshots = new(repository, cluster, workflows);
        snapshots.RegisterConfiguration(common.SnapshotParticipant);
        WorkflowDefinition invalid = new(ClusterConfigurationStore.Id, "ambiguous", [], []);
        WorkflowSaveResult rejected = await workflows.SaveAsync(new(Guid.NewGuid(), invalid, null));
        Assert.Equal(EOutcome.NotExecuted, rejected.Saved.Outcome);
        Assert.Equal("workflow.reservedId", Assert.Single(rejected.Validation).Code);
        StoredWorkflow legacy = new(invalid, ContractJson.Hash(invalid), DateTimeOffset.UtcNow, EActivationPolicy.Explicit);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("workflows", invalid.Id, null, NodeValue.FromObject(legacy))]);
        SnapshotCaptureResult capture = await snapshots.CaptureAsync(Guid.NewGuid(), new(invalid.Id, "one", ESnapshotKind.Configuration));
        Assert.Equal(EOutcome.NotExecuted, capture.Result.Outcome);
        Assert.Equal("Snapshot.TargetAmbiguous", capture.Result.Reason);
        Assert.Equal("UTC", common.Capture()!.Settings.TimeZoneId);
    }

    /// <summary>保存のみの自動スナップショットが旧実有効版を新保存版で捏造しません。</summary>
    /// <returns>保存時点の実有効版の確認完了です。</returns>
    [Fact]
    public async Task AutomaticSnapshotRecordsActualEffectiveVersionForSaveOnly()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Create(repository);
        await cluster.StartAsync();
        WorkflowStore store = Store(repository, cluster, new(StringComparer.Ordinal));
        WorkflowDefinition original = Definition("live", "a", "b");
        WorkflowSaveResult first = await store.SaveAsync(new(Guid.NewGuid(), original, null));
        WorkflowSaveResult saved = await store.SaveAsync(new(Guid.NewGuid(), original with { Name = "saved-only" }, first.Revision, EActivationPolicy.Explicit));
        Assert.Equal(EOutcome.Succeeded, saved.Snapshot.Outcome);
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync("snapshots");
        SnapshotRecord latest = entries.Select(entry => entry.Value!.Json.Deserialize<SnapshotRecord>(ContractJson.Options)!).MaxBy(snapshot => snapshot.CapturedAt)!;
        Assert.Equal("saved-only", latest.Value.Json.GetProperty("Saved").GetProperty("Definition").GetProperty("Name").GetString());
        Assert.Equal("live", latest.Value.Json.GetProperty("Effective").GetProperty("Name").GetString());
    }

    /// <summary>別Peerで受けた保存も共有保護を確認し、対象外ノードだけを変更できます。</summary>
    /// <returns>実UDP越しの保護と解除の検証の完了。</returns>
    [Fact]
    public async Task EditingProtectionIsSharedAcrossTwoPeers()
    {
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        SqliteRepository firstRepository = Repository();
        SqliteRepository secondRepository = Repository();
        ClusterOptions options = new(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, Path.GetTempPath()) { HeartbeatInterval = TimeSpan.FromMilliseconds(100) };
        PeerCluster first = new(options, firstRepository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        PeerCluster second = new(options with { PeerId = "two", NewCluster = false }, secondRepository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        WorkflowStore firstStore = Store(firstRepository, first, new(StringComparer.Ordinal));
        WorkflowStore secondStore = Store(secondRepository, second, new(StringComparer.Ordinal));
        SnapshotService firstSnapshots = new(firstRepository, first, firstStore);
        SnapshotService secondSnapshots = new(secondRepository, second, secondStore);
        await first.StartAsync();
        await second.StartAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(25, deadline.Token);
        }
        WorkflowDefinition definition = Definition("shared", "a", "b");
        WorkflowSaveResult saved = await firstStore.SaveAsync(new(Guid.NewGuid(), definition, null));
        Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
        SnapshotTarget target = new("demo", "one", ESnapshotKind.Configuration, ["a"]);
        SnapshotCaptureResult capture = await firstSnapshots.CaptureAsync(Guid.NewGuid(), target);
        RuntimeParticipant participant = new("pause", 1);
        firstSnapshots.RegisterRuntime(participant);
        SnapshotTarget runtime = new("pause", "one", ESnapshotKind.Runtime);
        SnapshotCaptureResult runtimeCapture = await firstSnapshots.CaptureAsync(Guid.NewGuid(), runtime);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        participant.RestoreWait = release.Task;
        SnapshotRestoreRequest request = new(Guid.NewGuid(), [new(target, capture.Snapshot!.Id), new(runtime, runtimeCapture.Snapshot!.Id)]);
        Task<SnapshotRestoreResult> restoring = firstSnapshots.RestoreAsync(request);
        try
        {
            while (participant.Restores == 0)
            {
                await Task.Delay(25, deadline.Token);
            }
            Assert.Equal(request.OperationId, Assert.Single(await secondSnapshots.GetEditLocksAsync()).OperationId);
            StoredWorkflow current = (await secondStore.GetAsync("demo"))!;
            WorkflowDefinition forbidden = current.Definition with { Nodes = [current.Definition.Nodes[0] with { Position = new(5, 5) }, current.Definition.Nodes[1]] };
            Assert.Equal(EOutcome.NotExecuted, (await secondStore.SaveAsync(new(Guid.NewGuid(), forbidden, current.Revision))).Saved.Outcome);
            WorkflowDefinition allowed = current.Definition with { Nodes = [current.Definition.Nodes[0], current.Definition.Nodes[1] with { Position = new(7, 7) }] };
            Assert.Equal(EOutcome.Succeeded, (await secondStore.SaveAsync(new(Guid.NewGuid(), allowed, current.Revision, EActivationPolicy.Explicit))).Shared.Outcome);
        }
        finally
        {
            release.TrySetResult();
            await restoring.ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, (await restoring.ConfigureAwait(true)).Outcome);
        Assert.Empty(await secondSnapshots.GetEditLocksAsync());
    }

    /// <summary>結果不明中も選択範囲を保護し、対象外保存を通し、元操作記録だけで回復します。</summary>
    /// <returns>保護継続と回復の検証の完了。</returns>
    [Fact]
    public async Task UnknownRestorationAllowsOnlyUnselectedEditsUntilOriginalRecovery()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Create(repository);
        await cluster.StartAsync();
        WorkflowStore store = Store(repository, cluster, new(StringComparer.Ordinal));
        SnapshotService snapshots = new(repository, cluster, store);
        WorkflowDefinition original = Definition("old", "a-old", "b-old");
        WorkflowSaveResult first = await store.SaveAsync(new(Guid.NewGuid(), original, null));
        SnapshotTarget target = new("demo", "one", ESnapshotKind.Configuration, ["a"]);
        SnapshotCaptureResult capture = await snapshots.CaptureAsync(Guid.NewGuid(), target);
        WorkflowDefinition current = Definition("current", "a-new", "b-new");
        await store.SaveAsync(new(Guid.NewGuid(), current, first.Revision));
        RuntimeParticipant runtime = new("independent", 1);
        snapshots.RegisterRuntime(runtime);
        SnapshotTarget runtimeTarget = new(runtime.Id, "one", ESnapshotKind.Runtime);
        SnapshotCaptureResult state = await snapshots.CaptureAsync(Guid.NewGuid(), runtimeTarget);
        runtime.Current = 2;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.RestoreWait = release.Task;
        SnapshotRestoreRequest request = new(Guid.NewGuid(), [new(target, capture.Snapshot!.Id), new(runtimeTarget, state.Snapshot!.Id)]);
        using CancellationTokenSource timeout = new();
        Task<SnapshotRestoreResult> restoration = snapshots.RestoreAsync(request, timeout.Token);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        try
        {
            while (runtime.Restores == 0)
            {
                await Task.Delay(10, deadline.Token);
            }
            await timeout.CancelAsync();
            SnapshotRestoreResult unknown = await restoration.ConfigureAwait(true);
            Assert.Equal(EOutcome.Unknown, unknown.Outcome);
            Assert.Single(await snapshots.GetEditLocksAsync());
            StoredWorkflow saved = (await store.GetAsync("demo"))!;
            WorkflowDefinition outside = saved.Definition with { Name = "outside", Nodes = [saved.Definition.Nodes[0], current.Nodes[1] with { Settings = JsonSerializer.SerializeToElement(new { scope = "workflow", key = "b-later" }) }] };
            WorkflowSaveResult allowed = await store.SaveAsync(new(Guid.NewGuid(), outside, saved.Revision, EActivationPolicy.Explicit));
            Assert.Equal(EOutcome.Succeeded, allowed.Saved.Outcome);
            WorkflowSaveResult rejected = await store.SaveAsync(new(Guid.NewGuid(), outside with { Nodes = [current.Nodes[0], outside.Nodes[1]] }, allowed.Revision));
            Assert.Equal(EOutcome.NotExecuted, rejected.Saved.Outcome);
            release.SetResult();
            SnapshotRestoreResult recovered = await snapshots.RecoverAsync(request);
            Assert.Equal(EOutcome.Succeeded, recovered.Outcome);
            Assert.Equal(1, runtime.Restores);
            Assert.Equal(ContractJson.Hash(outside), ContractJson.Hash((await store.GetAsync("demo"))!.Definition));
            Assert.Empty(await snapshots.GetEditLocksAsync());
        }
        finally
        {
            release.TrySetResult();
            await restoration.ConfigureAwait(true);
        }
    }

    /// <summary>選択ノードだけを古い値へ戻し、他ノードと全体属性は現在値を維持します。</summary>
    /// <returns>保存版と有効版の比較完了です。</returns>
    [Fact]
    public async Task SubsetRestorationPreservesUnselectedSavedAndEffectiveConfiguration()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Create(repository);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = Store(repository, cluster, effective);
        SnapshotService snapshots = new(repository, cluster, store);
        WorkflowDefinition old = Definition("old", "a-old", "b-old");
        old = old with { Nodes = [old.Nodes[0], old.Nodes[1] with { Type = "state.dedupe" }] };
        WorkflowSaveResult first = await store.SaveAsync(new(Guid.NewGuid(), old, null));
        SnapshotCaptureResult capture = await snapshots.CaptureAsync(Guid.NewGuid(), new("demo", "one", ESnapshotKind.Configuration));
        WorkflowDefinition current = Definition("current", "a-new", "b-new");
        current = current with { Nodes = [current.Nodes[0], current.Nodes[1] with { Type = "state.dedupe" }] };
        await store.SaveAsync(new(Guid.NewGuid(), current, first.Revision));
        snapshots.RegisterRuntime(new NodeStateSnapshotParticipant("workflow:demo", "workflow:demo", repository, cluster, () => effective.GetValueOrDefault("demo")?.Definition));
        SnapshotTarget target = new("demo", "one", ESnapshotKind.Configuration, ["a"]);
        SnapshotRestoreResult result = await snapshots.RestoreAsync(new(Guid.NewGuid(), [new(target, capture.Snapshot!.Id)]));
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        WorkflowDefinition expected = current with { Nodes = [old.Nodes[0], current.Nodes[1]] };
        Assert.Equal(ContractJson.Hash(expected), ContractJson.Hash((await store.GetAsync("demo"))!.Definition));
        Assert.Equal(ContractJson.Hash(expected), ContractJson.Hash(effective["demo"].Definition));
        Assert.Empty(await snapshots.GetEditLocksAsync());
        SnapshotCaptureResult partial = await snapshots.CaptureAsync(Guid.NewGuid(), target);
        SnapshotRestoreResult expanded = await snapshots.RestoreAsync(new(Guid.NewGuid(), [new(target with { NodeIds = null }, partial.Snapshot!.Id)]));
        Assert.Equal(EOutcome.NotExecuted, expanded.Outcome);
    }

    /// <summary>未保存範囲を明示取消し、対象外編集と別UIの未保存確認を維持します。</summary>
    /// <returns>開始拒否・部分取消・UI分離の確認完了です。</returns>
    [Fact]
    public async Task DraftResolutionDiscardsOnlySelectedNodesAndRequiresEachDirtyUi()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Create(repository);
        await cluster.StartAsync();
        WorkflowStore store = Store(repository, cluster, new(StringComparer.Ordinal));
        CollaborationService collaboration = new(store, cluster);
        SnapshotService snapshots = new(repository, cluster, store) { Editor = collaboration };
        WorkflowDefinition saved = Definition("saved", "a", "b");
        WorkflowSaveResult first = await store.SaveAsync(new(Guid.NewGuid(), saved, null));
        SnapshotTarget target = new("demo", "one", ESnapshotKind.Configuration, ["a"]);
        SnapshotCaptureResult capture = await snapshots.CaptureAsync(Guid.NewGuid(), target);
        Guid ui = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        WorkflowDefinition edited = Definition("unsaved-name", "a-edit", "b-edit");
        DraftView draft = await collaboration.ApplyChangesAsync(new(Guid.NewGuid(), "demo", ui, first.Revision, null, saved, edited, new Dictionary<string, EEditResolution>(StringComparer.Ordinal)));
        await collaboration.UpdatePresenceAsync(new("demo", ui, "host", "one", ["a"], Dirty: true, DirtyNodeIds: ["a", "b"]));
        await collaboration.UpdatePresenceAsync(new("demo", other, "host", "two", ["a"], Dirty: true, DirtyNodeIds: ["a"]));
        SnapshotRestoreRequest request = new(Guid.NewGuid(), [new(target, capture.Snapshot!.Id)]);
        Assert.False((await snapshots.PrepareRestoreAsync(request)).Ready);
        Assert.Equal(EOutcome.NotExecuted, (await snapshots.RestoreAsync(request)).Outcome);
        Assert.Empty(await snapshots.GetEditLocksAsync());
        SnapshotDraftResolution resolution = new(request, "one", "demo", ui, draft.SessionVersion, ESnapshotDraftAction.Discard);
        Assert.Equal(EOutcome.Succeeded, (await snapshots.ResolveDraftAsync(resolution)).Outcome);
        DraftView remaining = (await collaboration.GetDraftAsync("demo"))!;
        Assert.Equal("unsaved-name", remaining.Definition.Name);
        Assert.Equal(ContractJson.Hash(saved.Nodes[0]), ContractJson.Hash(remaining.Definition.Nodes[0]));
        Assert.Equal(ContractJson.Hash(edited.Nodes[1]), ContractJson.Hash(remaining.Definition.Nodes[1]));
        Assert.False((await snapshots.PrepareRestoreAsync(request)).Ready);
        Assert.Equal(EOutcome.Succeeded, (await snapshots.ResolveDraftAsync(resolution with { UiInstanceId = other, ExpectedSessionVersion = remaining.SessionVersion, Action = ESnapshotDraftAction.ConfirmSaved })).Outcome);
        Assert.True((await snapshots.PrepareRestoreAsync(request)).Ready);
        Assert.Equal(ContractJson.Hash(saved), ContractJson.Hash((await store.GetAsync("demo"))!.Definition));
    }

    /// <summary>Host Groupの指定を対象ノードへ展開し、未知Hostを黙って除外しません。</summary>
    /// <returns>範囲の展開結果です。</returns>
    [Fact]
    public async Task HostGroupScopeReturnsOnlyAssignedNodes()
    {
        SqliteRepository repository = Repository();
        await using PeerCluster cluster = Create(repository);
        await cluster.StartAsync();
        cluster.UpdateHostGroups(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["room"] = ["host"], ["empty"] = [] });
        WorkflowStore store = Store(repository, cluster, new(StringComparer.Ordinal));
        WorkflowDefinition definition = Definition("scope", "a", "b");
        definition = definition with { Nodes = [definition.Nodes[0] with { Target = new(EExecutionScope.All, Groups: ["room"]) }, definition.Nodes[1] with { Target = new(EExecutionScope.One, ["other"]) }] };
        await store.SaveAsync(new(Guid.NewGuid(), definition, null, EActivationPolicy.Explicit));
        SnapshotService snapshots = new(repository, cluster, store);
        SnapshotScopeResult result = await snapshots.ExpandScopeAsync(new("demo", HostGroups: ["room"]));
        Assert.Empty(result.Issues);
        Assert.Equal("a", Assert.Single(Assert.Single(result.Targets).NodeIds!));
        Assert.NotEmpty((await snapshots.ExpandScopeAsync(new("demo", HostIds: ["missing"]))).Issues);
        SnapshotScopeResult empty = await snapshots.ExpandScopeAsync(new("demo", HostGroups: ["empty"]));
        Assert.Empty(empty.Targets);
        Assert.NotEmpty(empty.Issues);
    }

    /// <summary>意味検証を通す二つの汎用ノードを作ります。</summary>
    /// <returns>比較用の設定です。</returns>
    private static WorkflowDefinition Definition(string name, string first, string second) => new("demo", name, [new("a", "state.get", JsonSerializer.SerializeToElement(new { scope = "workflow", key = first })), new("b", "state.get", JsonSerializer.SerializeToElement(new { scope = "workflow", key = second }))], []);

    /// <summary>保存と実有効版を別々に確認できるStoreです。</summary>
    /// <returns>検証用Storeです。</returns>
    private static WorkflowStore Store(IDurableRepository repository, PeerCluster cluster, Dictionary<string, CompiledWorkflow> effective) => new(repository, cluster, new WorkflowCompiler(new NodeRegistry(StateNodeRegistration.Create(new NodeStateStore(repository, "one")))))
    {
        GetEffective = id => effective.GetValueOrDefault(id),
        ActivateHandler = (workflow, token) =>
        {
            effective[workflow.Definition.Id] = workflow;
            return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
        },
    };

    /// <summary>独立した保存領域です。</summary>
    /// <returns>検証用リポジトリです。</returns>
    private static SqliteRepository Repository() => new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));

    /// <summary>実UDPを使う独立Peerです。</summary>
    /// <returns>開始可能なPeerです。</returns>
    private static PeerCluster Create(IDurableRepository repository)
    {
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        return new(new ClusterOptions(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, Path.GetTempPath()), repository) { ReadinessCheck = token => ValueTask.FromResult(true) };
    }
}
