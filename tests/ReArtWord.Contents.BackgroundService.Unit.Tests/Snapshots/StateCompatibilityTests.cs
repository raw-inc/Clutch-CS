using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
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

/// <summary>状態の利用契約と無関係な定義編集を区別します。</summary>
public sealed class StateCompatibilityTests
{
    /// <summary>状態を読むノードの動作契約が変わった場合、古い契約の状態を変更前に拒否します。</summary>
    /// <returns>実保存値を古い状態へ戻さない確認。</returns>
    [Fact]
    public async Task StateBehaviorVersionChangeRefusesOldSnapshotBeforeMutationAsync()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        PeerCluster cluster = Create(repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = cluster.ConfigureAwait(true);
        await cluster.StartAsync().ConfigureAwait(true);
        NodeDefinition node = new("read", "state.get", JsonSerializer.SerializeToElement(new { key = "key" }));
        WorkflowDefinition definition = new("demo", "versioned state", [node], []);
        SnapshotService snapshots = new(repository, cluster, new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))));
        NodeStateSnapshotParticipant participant = new("workflow:demo", "workflow:demo", repository, cluster, () => definition);
        snapshots.RegisterRuntime(participant);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("workflow:demo", "key", null, NodeValue.FromObject("old"))]).ConfigureAwait(true);
        SnapshotTarget target = new(participant.Id, "one", ESnapshotKind.Runtime);
        SnapshotCaptureResult captured = await snapshots.CaptureAsync(Guid.NewGuid(), target).ConfigureAwait(true);
        DurableEntry current = (await repository.ReadAsync("workflow:demo", "key").ConfigureAwait(true))!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("workflow:demo", "key", current.Version, NodeValue.FromObject("current"))]).ConfigureAwait(true);
        definition = definition with { Nodes = [node with { ContractVersion = 2 }] };
        SnapshotRestoreResult rejected = await snapshots.RestoreAsync(new SnapshotRestoreRequest(Guid.NewGuid(), [new(target, captured.Snapshot!.Id)])).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, rejected.Outcome);
        Assert.Equal("Snapshot.IncompatibleStateContract", rejected.Reason);
        Assert.Equal("current", (await repository.ReadAsync("workflow:demo", "key").ConfigureAwait(true))!.Value!.GetString());
        definition = definition with { Nodes = [node with { ContractVersion = 1 }] };
        Assert.Equal(EOutcome.Succeeded, (await snapshots.RestoreAsync(new SnapshotRestoreRequest(Guid.NewGuid(), [new(target, captured.Snapshot.Id)])).ConfigureAwait(true)).Outcome);
        Assert.Equal("old", (await repository.ReadAsync("workflow:demo", "key").ConfigureAwait(true))!.Value!.GetString());
    }

    /// <summary>設定だけでは不成立な組合せでも、対応状態を同時選択すれば復元できます。</summary>
    /// <returns>準備と実適用の検証完了。</returns>
    [Fact]
    public async Task SelectedConfigurationAndRuntimeAreValidatedTogether()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        await using PeerCluster cluster = Create(repository);
        await cluster.StartAsync();
        CompiledWorkflow? effective = null;
        WorkflowCompiler compiler = new(new NodeRegistry(StateNodeRegistration.Create(new NodeStateStore(repository, "one"))));
        WorkflowStore workflows = new(repository, cluster, compiler)
        {
            GetEffective = id => effective,
            ActivateHandler = (workflow, token) =>
            {
                effective = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
        WorkflowDefinition original = new("demo", "old", [], []);
        WorkflowSaveResult first = await workflows.SaveAsync(new WorkflowSaveRequest(Guid.NewGuid(), original, null));
        Assert.Equal(EOutcome.Succeeded, first.Activation.Outcome);
        SnapshotService snapshots = new(repository, cluster, workflows);
        NodeStateSnapshotParticipant participant = new("workflow:demo", "workflow:demo", repository, cluster, () => effective?.Definition);
        snapshots.RegisterRuntime(participant);
        SnapshotTarget configurationTarget = new("demo", "one", ESnapshotKind.Configuration);
        SnapshotTarget runtimeTarget = new(participant.Id, "one", ESnapshotKind.Runtime);
        SnapshotCaptureResult configuration = await snapshots.CaptureAsync(Guid.NewGuid(), configurationTarget);
        SnapshotCaptureResult runtime = await snapshots.CaptureAsync(Guid.NewGuid(), runtimeTarget);
        WorkflowDefinition changed = original with { Nodes = [new NodeDefinition("marker", "state.dedupe", JsonSerializer.SerializeToElement(new { scope = "workflow", key = "new" }))] };
        WorkflowSaveResult second = await workflows.SaveAsync(new WorkflowSaveRequest(Guid.NewGuid(), changed, first.Revision));
        Assert.Equal(EOutcome.Succeeded, second.Activation.Outcome);
        SnapshotSelection configurationSelection = new(configurationTarget, configuration.Snapshot!.Id);
        SnapshotRestoreResult rejected = await snapshots.RestoreAsync(new SnapshotRestoreRequest(Guid.NewGuid(), [configurationSelection]));
        Assert.Equal(EOutcome.NotExecuted, rejected.Outcome);
        Assert.Single(effective!.Definition.Nodes);
        SnapshotRestoreResult combined = await snapshots.RestoreAsync(new SnapshotRestoreRequest(Guid.NewGuid(), [new SnapshotSelection(runtimeTarget, runtime.Snapshot!.Id), configurationSelection]));
        Assert.Equal(EOutcome.Succeeded, combined.Outcome);
        Assert.Empty(effective.Definition.Nodes);
    }

    /// <summary>契約変更は変更前に拒否し、名称変更なら自由値を復元します。</summary>
    /// <returns>実保存と復元の検証完了。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeRestoreChecksContractBeforeChangingValues(bool changeContract)
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        await using PeerCluster cluster = Create(repository);
        await cluster.StartAsync();
        WorkflowDefinition definition = new("demo", "old", [], []);
        WorkflowStore workflows = new(repository, cluster, new WorkflowCompiler(new NodeRegistry([])));
        SnapshotService snapshots = new(repository, cluster, workflows);
        NodeStateSnapshotParticipant participant = new("workflow:demo", "workflow:demo", repository, cluster, () => definition);
        snapshots.RegisterRuntime(participant);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("workflow:demo", "key", null, NodeValue.FromObject("old"))]);
        SnapshotTarget target = new(participant.Id, "one", ESnapshotKind.Runtime);
        SnapshotCaptureResult capture = await snapshots.CaptureAsync(Guid.NewGuid(), target);
        Assert.Equal(EOutcome.Succeeded, capture.Result.Outcome);
        DurableEntry old = (await repository.ReadAsync("workflow:demo", "key"))!;
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("workflow:demo", "key", old.Version, NodeValue.FromObject("current"))]);
        definition = definition with { Name = "renamed" };
        if (changeContract)
        {
            definition = definition with { Nodes = [new NodeDefinition("dedupe", "state.dedupe", JsonSerializer.SerializeToElement(new { key = "key" }))] };
        }
        SnapshotRestoreResult restored = await snapshots.RestoreAsync(new SnapshotRestoreRequest(Guid.NewGuid(), [new SnapshotSelection(target, capture.Snapshot!.Id)]));
        if (changeContract)
        {
            Assert.Equal(EOutcome.NotExecuted, restored.Outcome);
            Assert.Equal("Snapshot.IncompatibleStateContract", restored.Reason);
            Assert.Equal("current", (await repository.ReadAsync("workflow:demo", "key"))!.Value!.GetString());
        }
        else
        {
            Assert.Equal(EOutcome.Succeeded, restored.Outcome);
            Assert.Equal("old", (await repository.ReadAsync("workflow:demo", "key"))!.Value!.GetString());
        }
    }

    /// <summary>既知契約のない旧形式を復元可能とみなしません。</summary>
    /// <returns>形式検証完了。</returns>
    [Fact]
    public async Task LegacyBareStateHasUnknownCompatibility()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        await using PeerCluster cluster = Create(repository);
        await cluster.StartAsync();
        NodeStateSnapshotParticipant participant = new("workflow:demo", "workflow:demo", repository, cluster, () => new WorkflowDefinition("demo", "demo", [], []));
        Assert.NotEmpty(await participant.ValidateAsync(NodeValue.FromObject(new { key = "legacy" })));
    }

    /// <summary>実UDPを用いる独立Peerを作ります。</summary>
    /// <returns>Ready条件を持つPeerです。</returns>
    private static PeerCluster Create(IDurableRepository repository)
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        return new(new ClusterOptions(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, Path.GetTempPath()), repository)
        {
            ReadinessCheck = token => ValueTask.FromResult(true),
        };
    }
}
