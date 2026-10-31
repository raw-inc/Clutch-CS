using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>保存・スナップショット・実有効化と編集競合を検証します。</summary>
public sealed class WorkflowStoreTests : IDisposable
{
    private readonly MediaTestFiles files = new();

    /// <summary>確認した候補が変われば拒否し、新しい比較だけを反映して再送は元結果を返します。</summary>
    /// <returns>差分と確認対象版の検証完了です。</returns>
    [Fact]
    public async Task ImpactConfirmationRejectsStaleCandidateAndReplaysOriginalResultAsync()
    {
        FailingRepository repository = CreateRepository();
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = CreateStore(repository, cluster, effective);
        WorkflowDefinition old = SettingsWorkflow(1);
        WorkflowSaveResult initial = await store.SaveAsync(new(Guid.NewGuid(), old, null));
        WorkflowSaveResult pending = await store.SaveAsync(new(Guid.NewGuid(), SettingsWorkflow(2), initial.Revision, EActivationPolicy.ConfirmImpact));
        ConfigurationImpactPreview preview = await store.InspectImpactAsync(old.Id);
        Assert.Contains(preview.Differences, change => change.Path.StartsWith("/nodes/pass/inputs/value", StringComparison.Ordinal));
        Assert.Equal(initial.Revision, preview.EffectiveRevision);
        ConfigurationPhaseResult missing = await store.ActivateAsync(new(Guid.NewGuid(), old.Id, pending.Revision!, true));
        Assert.Equal(EOutcome.NotExecuted, missing.Outcome);
        WorkflowSaveResult next = await store.SaveAsync(new(Guid.NewGuid(), SettingsWorkflow(3), pending.Revision, EActivationPolicy.ConfirmImpact));
        ConfigurationPhaseResult stale = await store.ActivateAsync(new(Guid.NewGuid(), old.Id, pending.Revision!, true, preview.Token));
        Assert.Equal(EOutcome.NotExecuted, stale.Outcome);
        Assert.Equal(initial.Revision, effective[old.Id].Revision);
        ConfigurationImpactPreview current = await store.InspectImpactAsync(old.Id);
        ActivationRequest request = new(Guid.NewGuid(), old.Id, next.Revision!, true, current.Token);
        ConfigurationPhaseResult activated = await store.ActivateAsync(request);
        Assert.Equal(EOutcome.Succeeded, activated.Outcome);
        Assert.Equal(next.Revision, effective[old.Id].Revision);
        Assert.Equal(activated, await store.ActivateAsync(request));
    }

    /// <summary>未反映の値変更を配置変更だけの追加保存で自動反映しません。</summary>
    /// <returns>保存・有効版と適用方針の確認です。</returns>
    [Fact]
    public async Task PendingValuesKeepConfirmationDuringLayoutSaveAsync()
    {
        FailingRepository repository = CreateRepository();
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = CreateStore(repository, cluster, effective);
        WorkflowDefinition old = SettingsWorkflow(1);
        WorkflowSaveResult initial = await store.SaveAsync(new(Guid.NewGuid(), old, null));
        WorkflowDefinition rules = old with { ActivationRules = new Dictionary<EWorkflowChangeKind, EActivationPolicy> { [EWorkflowChangeKind.Values] = EActivationPolicy.ConfirmImpact } };
        WorkflowSaveResult configured = await store.SaveAsync(new(Guid.NewGuid(), rules, initial.Revision));
        Assert.Equal(EOutcome.Succeeded, configured.Activation.Outcome);
        WorkflowDefinition edited = SettingsWorkflow(2) with { ActivationRules = rules.ActivationRules };
        WorkflowSaveResult pending = await store.SaveAsync(new(Guid.NewGuid(), edited, configured.Revision));
        Assert.Equal(EOutcome.NotExecuted, pending.Activation.Outcome);
        WorkflowDefinition moved = edited with { Nodes = [edited.Nodes[0] with { Position = new(20, 30) }] };
        WorkflowSaveResult saved = await store.SaveAsync(new(Guid.NewGuid(), moved, pending.Revision));
        Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
        Assert.Equal(EOutcome.NotExecuted, saved.Activation.Outcome);
        Assert.Equal(EActivationPolicy.ConfirmImpact, (await store.GetAsync(old.Id))!.ActivationPolicy);
        Assert.Equal(configured.Revision, effective[old.Id].Revision);
    }

    /// <summary>同じ保存で方針を緩めても元有効版の確認を必要とします。</summary>
    [Fact]
    public void RuleRemovalCannotBypassPreviousConfirmation()
    {
        WorkflowDefinition original = SettingsWorkflow(1) with
        {
            ActivationRules = new Dictionary<EWorkflowChangeKind, EActivationPolicy>
            {
                [EWorkflowChangeKind.Values] = EActivationPolicy.Explicit,
                [EWorkflowChangeKind.Policy] = EActivationPolicy.ConfirmImpact,
            },
        };
        WorkflowDefinition candidate = SettingsWorkflow(2);
        Assert.Equal(EActivationPolicy.ConfirmImpact, WorkflowActivationPolicy.Resolve(original, candidate, EActivationPolicy.Auto));
        Assert.Contains(EWorkflowChangeKind.Values, WorkflowChangeInspection.Kinds(original, candidate));
        Assert.Contains(EWorkflowChangeKind.Policy, WorkflowChangeInspection.Kinds(original, candidate));
    }

    /// <summary>変更前の保存版・有効版を保護し、そのスナップショットから実際に復元できます。</summary>
    /// <returns>保存、再送、復元の検証完了です。</returns>
    [Fact]
    public async Task BeforeSettingsSnapshotRestoresPreviousDefinitionAsync()
    {
        FailingRepository repository = CreateRepository();
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = CreateStore(repository, cluster, effective);
        SnapshotService snapshots = new(repository, cluster, store);
        WorkflowDefinition old = SettingsWorkflow(1);
        WorkflowSaveResult first = await store.SaveAsync(new(Guid.NewGuid(), old, null));
        Assert.Equal(EOutcome.Succeeded, first.Activation.Outcome);
        WorkflowSaveRequest request = new(Guid.NewGuid(), SettingsWorkflow(2), first.Revision);
        WorkflowSaveResult saved = await store.SaveAsync(request);
        Assert.Equal(EOutcome.Succeeded, saved.BeforeSnapshot?.Outcome);
        Assert.Equal(EOutcome.Succeeded, saved.Snapshot.Outcome);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        int count = (await repository.ListAsync("snapshots")).Count;
        WorkflowSaveResult replay = await store.SaveAsync(request);
        Assert.Equal(saved.BeforeSnapshotId, replay.BeforeSnapshotId);
        Assert.Equal(count, (await repository.ListAsync("snapshots")).Count);
        SnapshotTarget target = new(old.Id, cluster.LocalPeerId, ESnapshotKind.Configuration);
        SnapshotRestoreResult restored = await snapshots.RestoreAsync(new(Guid.NewGuid(), [new(target, saved.BeforeSnapshotId)]));
        Assert.Equal(EOutcome.Succeeded, restored.Outcome);
        Assert.Equal(ContractJson.Hash(old), ContractJson.Hash((await store.GetAsync(old.Id))!.Definition));
        Assert.Equal(ContractJson.Hash(old), effective[old.Id].Revision);
    }

    /// <summary>保護失敗で本体保存を開始せず、同じIDの再送でも新しく取得しません。</summary>
    /// <returns>変更を開始しない境界の確認です。</returns>
    [Fact]
    public async Task BeforeSettingsSnapshotFailurePreventsSaveAsync()
    {
        FailingRepository repository = CreateRepository();
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = CreateStore(repository, cluster, effective);
        WorkflowDefinition old = SettingsWorkflow(1);
        WorkflowSaveResult first = await store.SaveAsync(new(Guid.NewGuid(), old, null));
        repository.FailScope = "snapshots";
        WorkflowSaveRequest request = new(Guid.NewGuid(), SettingsWorkflow(2), first.Revision);
        WorkflowSaveResult failed = await store.SaveAsync(request);
        Assert.Equal(EOutcome.Failed, failed.BeforeSnapshot?.Outcome);
        Assert.Equal(EOutcome.NotExecuted, failed.Saved.Outcome);
        Assert.Equal(EOutcome.NotExecuted, failed.Activation.Outcome);
        Assert.Equal(first.Revision, (await store.GetAsync(old.Id))!.Revision);
        Assert.Equal(first.Revision, effective[old.Id].Revision);
        repository.FailScope = null;
        WorkflowSaveResult replay = await store.SaveAsync(request);
        Assert.Equal(failed.BeforeSnapshot, replay.BeforeSnapshot);
        Assert.Equal(EOutcome.NotExecuted, replay.Saved.Outcome);
        Assert.Single(await repository.ListAsync("snapshots"));
    }

    /// <summary>表示位置だけの変更には変更前保護を要求せず、保存時取得の失敗と区別します。</summary>
    /// <returns>二つの取得契機の確認です。</returns>
    [Fact]
    public async Task LayoutChangeDoesNotRequireBeforeSettingsSnapshotAsync()
    {
        FailingRepository repository = CreateRepository();
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        WorkflowStore store = CreateStore(repository, cluster, new(StringComparer.Ordinal));
        WorkflowDefinition old = SettingsWorkflow(1);
        WorkflowSaveResult first = await store.SaveAsync(new(Guid.NewGuid(), old, null));
        WorkflowDefinition moved = old with { Nodes = [old.Nodes[0] with { Position = new(20, 30) }] };
        repository.FailScope = "snapshots";
        WorkflowSaveResult result = await store.SaveAsync(new(Guid.NewGuid(), moved, first.Revision));
        Assert.Null(result.BeforeSnapshot);
        Assert.Equal(EOutcome.Succeeded, result.Saved.Outcome);
        Assert.Equal(EOutcome.Failed, result.Snapshot.Outcome);
    }

    /// <summary>値だけが異なる実行可能な設定を作成します。</summary>
    /// <returns>通常のpassノードを持つ定義です。</returns>
    private static WorkflowDefinition SettingsWorkflow(int value) => new("settings", "Settings", [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }),
        new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) })], []);

    /// <summary>HTTP JSONでも項目削除と明示nullの候補を区別できます。</summary>
    [Fact]
    public void MergeConflictRetainsExistenceAcrossJsonRoundTrip()
    {
        NodeDefinition originalNode = new("node", "draft", JsonSerializer.SerializeToElement(new { value = 1 }));
        WorkflowDefinition original = new("demo", "name", [originalNode], []);
        WorkflowDefinition current = original with { Nodes = [originalNode with { Settings = JsonSerializer.SerializeToElement(new { }) }] };
        WorkflowDefinition edited = original with { Nodes = [originalNode with { Settings = JsonSerializer.SerializeToElement(new { value = (string?)null }) }] };
        WorkflowMergeRequest request = new(Guid.NewGuid(), "base", edited, new Dictionary<string, EEditResolution>(StringComparer.Ordinal));
        EditConflict conflict = Assert.Single(WorkflowEditorMerge.Merge(original, current, request).Conflicts);
        EditConflict roundTrip = ContractJson.Copy(conflict);
        Assert.True(roundTrip.BaseExists);
        Assert.False(roundTrip.CurrentExists);
        Assert.True(roundTrip.EditedExists);
        WorkflowMergeResult chosen = WorkflowEditorMerge.Merge(original, current, request with { Resolutions = new Dictionary<string, EEditResolution>(StringComparer.Ordinal) { [conflict.Path] = EEditResolution.UseEdited } });
        Assert.Equal(JsonValueKind.Null, chosen.Candidate!.Nodes[0].Settings.GetProperty("value").ValueKind);
    }

    /// <summary>スナップショット失敗でも保存成功は維持し、旧effectiveを保ちます。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task SnapshotFailureKeepsSavedAndPreviousEffectiveSeparate()
    {
        FailingRepository repository = CreateRepository();
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = CreateStore(repository, cluster, effective);
        WorkflowDefinition old = new("demo", "old", [], []);
        WorkflowSaveResult first = await store.SaveAsync(new WorkflowSaveRequest(Guid.NewGuid(), old, null));
        Assert.Equal(EOutcome.Succeeded, first.Activation.Outcome);
        repository.FailScope = "snapshots";
        WorkflowSaveRequest request = new(Guid.NewGuid(), old with { Name = "new" }, first.Revision);
        WorkflowSaveResult failedSnapshot = await store.SaveAsync(request);
        Assert.Equal(EOutcome.Succeeded, failedSnapshot.Saved.Outcome);
        Assert.Equal(EOutcome.Succeeded, failedSnapshot.Shared.Outcome);
        Assert.Equal(EOutcome.Failed, failedSnapshot.Snapshot.Outcome);
        Assert.Equal(EOutcome.NotExecuted, failedSnapshot.Activation.Outcome);
        Assert.Equal("new", (await store.GetAsync("demo"))!.Definition.Name);
        Assert.Equal("old", effective["demo"].Definition.Name);
        WorkflowSaveResult duplicate = await store.SaveAsync(request);
        Assert.Equal(failedSnapshot.Snapshot, duplicate.Snapshot);
        Assert.Equal(failedSnapshot.Revision, duplicate.Revision);
        WorkflowSaveResult? queried = await store.GetSaveOperationAsync(request.OperationId);
        Assert.Equal(failedSnapshot.Shared, queried!.Shared);
        Assert.Equal(failedSnapshot.Snapshot, queried.Snapshot);
        Assert.Equal(failedSnapshot.Activation, queried.Activation);
        Dictionary<string, CompiledWorkflow> restarted = new(StringComparer.Ordinal);
        WorkflowStore afterRestart = CreateStore(repository, cluster, restarted);
        await afterRestart.RestoreEffectiveAsync();
        Assert.Equal("old", restarted["demo"].Definition.Name);
    }

    /// <summary>非競合編集は統合し、同項目の衝突は人の選択まで保存しません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task MergeUsesBaseAndKeepsConcurrentConflicts()
    {
        FailingRepository repository = CreateRepository();
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        WorkflowStore store = CreateStore(repository, cluster, new Dictionary<string, CompiledWorkflow>(StringComparer.Ordinal));
        WorkflowDefinition original = new("demo", "original", [], []);
        WorkflowSaveResult seed = await store.SaveAsync(new WorkflowSaveRequest(Guid.NewGuid(), original, null, EActivationPolicy.Explicit));
        await store.SaveAsync(new WorkflowSaveRequest(Guid.NewGuid(), original with { Name = "other" }, seed.Revision, EActivationPolicy.Explicit));
        WorkflowMergeResult merged = await store.MergeAsync(new WorkflowMergeRequest(Guid.NewGuid(), seed.Revision!, original with { Enabled = true }, new Dictionary<string, EEditResolution>(StringComparer.Ordinal), EActivationPolicy.ConfirmImpact));
        Assert.Empty(merged.Conflicts);
        Assert.Equal("other", merged.Candidate!.Name);
        Assert.True(merged.Candidate.Enabled);
        Assert.Equal(EOutcome.Succeeded, merged.Save!.Saved.Outcome);
        Assert.Equal(EOutcome.NotExecuted, merged.Save.Activation.Outcome);
        Assert.Equal(EActivationPolicy.ConfirmImpact, (await store.GetAsync("demo"))!.ActivationPolicy);
        WorkflowMergeResult conflict = await store.MergeAsync(new WorkflowMergeRequest(Guid.NewGuid(), seed.Revision!, original with { Name = "mine" }, new Dictionary<string, EEditResolution>(StringComparer.Ordinal)));
        Assert.Single(conflict.Conflicts);
        Assert.Null(conflict.Save);
        Assert.Equal("other", (await store.GetAsync("demo"))!.Definition.Name);
    }

    /// <summary>実有効計画を別に保持するroot側の境界を再現します。</summary>
    /// <returns>設定管理。</returns>
    private static WorkflowStore CreateStore(IDurableRepository repository, PeerCluster cluster, Dictionary<string, CompiledWorkflow> effective)
    {
        WorkflowStore store = new(repository, cluster, new WorkflowCompiler(new NodeRegistry([new PassNode()])))
        {
            GetEffective = id => effective.GetValueOrDefault(id),
            ActivateHandler = (workflow, token) =>
            {
                effective[workflow.Definition.Id] = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
        return store;
    }

    /// <summary>全Peerの終了後に試験用の保存先を削除します。</summary>
    public void Dispose() => files.Dispose();

    /// <summary>独立SQLite保存先を作ります。</summary>
    /// <returns>用途別失敗を設定できる保存先。</returns>
    private FailingRepository CreateRepository() => new(new SqliteRepository(Path.Combine(files.Root, Guid.NewGuid().ToString("N"), "state.db")));

    /// <summary>明示初期化した一Peerを作ります。</summary>
    /// <returns>独立Peer。</returns>
    private static PeerCluster CreateCluster(IDurableRepository repository)
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        return new PeerCluster(new ClusterOptions(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, Path.GetTempPath()), repository)
        {
            ReadinessCheck = token => ValueTask.FromResult(true),
        };
    }
}
