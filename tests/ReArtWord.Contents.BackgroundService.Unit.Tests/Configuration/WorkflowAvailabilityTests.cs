using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>用途別の保存不能方針を実ワークフロー保存・明示適用・復旧へ接続して検証します。</summary>
public sealed class WorkflowAvailabilityTests
{
    /// <summary>未保存候補の採用結果が不明でも別操作による同候補再適用を行いません。</summary>
    /// <returns>保存失敗と採用不明を別に保つ結果です。</returns>
    [Fact]
    public async Task UnknownCandidateAdoptionIsNotExecutedAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-workflow-unknown-" + Guid.NewGuid().ToString("N"));
        FailingRepository repository = new(new SqliteRepository(Path.Combine(root, "state.db")));
        using AvailabilityService availability = new("one", new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Configuration] = new(EAvailabilitySaveFailure.UseUnsaved) }), repository, root);
        await using PeerCluster cluster = Create(root, repository);
        await cluster.StartAsync().ConfigureAwait(true);
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = Store(repository, cluster, availability, effective);
        WorkflowDefinition original = new("demo", "original", [], []);
        WorkflowSaveResult initial = await store.SaveAsync(new(Guid.NewGuid(), original, null));
        int calls = 0;
        store.ActivateHandler = (workflow, token) =>
        {
            calls++;
            return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Unknown));
        };
        repository.FailScope = "workflows";
        WorkflowSaveRequest request = new(Guid.NewGuid(), original with { Name = "candidate" }, initial.Revision);
        WorkflowSaveResult result = await store.SaveAsync(request);
        Assert.Equal(EOutcome.Failed, result.Saved.Outcome);
        Assert.Equal(EOutcome.Unknown, result.Activation.Outcome);
        Assert.Equal(EOutcome.Unknown, (await store.SaveAsync(request)).Activation.Outcome);
        Assert.Equal(EOutcome.Unknown, (await store.ActivateAsync(new(Guid.NewGuid(), "demo", result.Revision!))).Outcome);
        Assert.Equal(1, calls);
    }

    /// <summary>実媒体の書込失敗でも確認済み保存版を変えず、指定した代替媒体だけから復旧します。</summary>
    /// <returns>実IO故障・未保存表示・新サービスの復旧結果です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealJsonWriteFailureUsesOnlyExplicitRecoveryBackend(bool fallback, bool newWorkflow)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-workflow-io-" + Guid.NewGuid().ToString("N"));
        IDurableRepository repository = await RepositoryFactory.CreateAsync(root, "one", new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Configuration] = EStorageBackend.Json }));
        EStorageBackend? backend = null;
        if (fallback)
        {
            backend = EStorageBackend.Sqlite;
        }
        AvailabilityOptions options = new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Configuration] = new(EAvailabilitySaveFailure.UseUnsaved, FallbackBackend: backend) });
        using AvailabilityService availability = new("one", options, repository, root);
        await using PeerCluster cluster = Create(root, repository);
        await cluster.StartAsync().ConfigureAwait(true);
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = Store(repository, cluster, availability, effective);
        WorkflowDefinition definition = new("demo", "durable", [], []);
        WorkflowSaveResult? initial = null;
        string? durableName = null;
        if (!newWorkflow)
        {
            initial = await store.SaveAsync(new(Guid.NewGuid(), definition, null));
            durableName = "durable";
        }
        string obstruction = Path.Combine(root, "state.json.staging");
        Directory.CreateDirectory(obstruction);
        WorkflowSaveResult result = await store.SaveAsync(new(Guid.NewGuid(), definition with { Name = "candidate" }, initial?.Revision));
        Assert.Equal(EOutcome.Succeeded, result.Activation.Outcome);
        Assert.Equal(EOutcome.NotExecuted, result.Shared.Outcome);
        Assert.Equal(durableName, (await store.GetAsync("demo"))?.Definition.Name);
        Assert.Equal("candidate", effective["demo"].Definition.Name);
        Assert.Equal(!fallback, Assert.Single(availability.GetStates()).IsUnsaved);
        Directory.Delete(obstruction);
        using AvailabilityService restartedAvailability = new("one", options, repository, root);
        Dictionary<string, CompiledWorkflow> restarted = new(StringComparer.Ordinal);
        WorkflowStore restored = Store(repository, cluster, restartedAvailability, restarted);
        await restored.RestoreEffectiveAsync();
        string? expected = durableName;
        if (fallback)
        {
            expected = "candidate";
        }
        Assert.Equal(expected, restarted.GetValueOrDefault("demo")?.Definition.Name);
        await restored.PrepareLocalAsync();
        Assert.Equal(expected, restarted.GetValueOrDefault("demo")?.Definition.Name);
    }

    /// <summary>既定は旧版維持、明示Bだけ未保存有効とし、再確認・再送で戻したり重複適用しません。</summary>
    /// <returns>実保存段階とmemory有効化の区別です。</returns>
    [Theory]
    [InlineData(EAvailabilitySaveFailure.KeepConfirmed)]
    [InlineData(EAvailabilitySaveFailure.UseUnsaved)]
    public async Task SaveFailurePolicyControlsActualEffectiveWithoutChangingRecovery(EAvailabilitySaveFailure policy)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-workflow-availability-" + Guid.NewGuid().ToString("N"));
        FailingRepository repository = new(new SqliteRepository(Path.Combine(root, "state.db")));
        using AvailabilityService availability = new("one", new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Configuration] = new(policy) }), repository, root);
        await using PeerCluster cluster = Create(root, repository);
        await cluster.StartAsync().ConfigureAwait(true);
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = Store(repository, cluster, availability, effective);
        WorkflowDefinition original = new("demo", "original", [], []);
        WorkflowSaveResult initial = await store.SaveAsync(new(Guid.NewGuid(), original, null)).ConfigureAwait(true);
        repository.FailScope = "workflows";
        WorkflowSaveRequest request = new(Guid.NewGuid(), original with { Name = "candidate" }, initial.Revision);
        WorkflowSaveResult failed = await store.SaveAsync(request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, failed.Saved.Outcome);
        Assert.Equal(EOutcome.NotExecuted, failed.Shared.Outcome);
        Assert.Equal("original", (await store.GetAsync("demo").ConfigureAwait(true))!.Definition.Name);
        Assert.Equal(policy == EAvailabilitySaveFailure.UseUnsaved, Assert.Single(availability.GetStates()).IsUnsaved);
        await store.PrepareLocalAsync().ConfigureAwait(true);
        string expected = "original";
        if (policy == EAvailabilitySaveFailure.UseUnsaved)
        {
            expected = "candidate";
            Assert.Equal(EOutcome.Succeeded, failed.Activation.Outcome);
        }
        Assert.Equal(expected, effective["demo"].Definition.Name);
        await store.SaveAsync(request).ConfigureAwait(true);
        Assert.Equal(expected, effective["demo"].Definition.Name);
        Dictionary<string, CompiledWorkflow> restarted = new(StringComparer.Ordinal);
        WorkflowStore restored = Store(repository, cluster, null, restarted);
        await restored.RestoreEffectiveAsync().ConfigureAwait(true);
        Assert.Equal("original", restarted["demo"].Definition.Name);
    }

    /// <summary>明示確認後だけ候補を採用し、後のスナップショット失敗でも旧未保存有効状態を表示し続けます。</summary>
    /// <returns>候補・保存・実有効の三つの状態です。</returns>
    [Fact]
    public async Task ExplicitCandidateAndLaterSnapshotFailureKeepTheirOwnPhases()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-workflow-availability-" + Guid.NewGuid().ToString("N"));
        FailingRepository repository = new(new SqliteRepository(Path.Combine(root, "state.db")));
        using AvailabilityService availability = new("one", new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Configuration] = new(EAvailabilitySaveFailure.UseUnsaved) }), repository, root);
        await using PeerCluster cluster = Create(root, repository);
        await cluster.StartAsync().ConfigureAwait(true);
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = Store(repository, cluster, availability, effective);
        WorkflowDefinition original = new("demo", "original", [], []);
        WorkflowSaveResult initial = await store.SaveAsync(new(Guid.NewGuid(), original, null)).ConfigureAwait(true);
        repository.FailScope = "workflows";
        WorkflowSaveResult failed = await store.SaveAsync(new(Guid.NewGuid(), original with { Name = "candidate" }, initial.Revision, EActivationPolicy.ConfirmImpact)).ConfigureAwait(true);
        Assert.Equal("original", effective["demo"].Definition.Name);
        Assert.True(Assert.Single(availability.GetStates()).CandidateIsUnsaved);
        Assert.False(Assert.Single(availability.GetStates()).IsUnsaved);
        Assert.Equal(EOutcome.NotExecuted, (await store.ActivateAsync(new(Guid.NewGuid(), "demo", failed.Revision!)).ConfigureAwait(true)).Outcome);
        ConfigurationImpactPreview impact = await store.InspectImpactAsync("demo").ConfigureAwait(true);
        Assert.True(impact.UnsavedCandidate);
        Assert.Equal(EOutcome.Succeeded, (await store.ActivateAsync(new(Guid.NewGuid(), "demo", failed.Revision!, true, impact.Token)).ConfigureAwait(true)).Outcome);
        repository.FailScope = "snapshots";
        WorkflowSaveResult later = await store.SaveAsync(new(Guid.NewGuid(), original with { Name = "saved-later" }, initial.Revision)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, later.Shared.Outcome);
        Assert.Equal(EOutcome.Failed, later.Snapshot.Outcome);
        Assert.Equal("candidate", effective["demo"].Definition.Name);
        AvailabilityState state = Assert.Single(availability.GetStates());
        Assert.Equal(later.Revision, state.SavedRevision);
        Assert.Equal(failed.Revision, state.EffectiveRevision);
        Assert.True(state.IsUnsaved);
        await store.PrepareLocalAsync().ConfigureAwait(true);
        Assert.Equal("candidate", effective["demo"].Definition.Name);
        DurableEntry effectiveRow = (await repository.ReadAsync("peer:one:effective", "demo").ConfigureAwait(true))!;
        Assert.Equal("original", effectiveRow.Value!.Json.Deserialize<StoredWorkflow>()!.Definition.Name);
    }

    /// <summary>独立ポートで通常参加する一Peerを用意します。</summary>
    /// <returns>実保存・複製を行うPeerです。</returns>
    private static PeerCluster Create(string root, IDurableRepository repository)
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        return new(new(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, root), repository) { ReadinessCheck = token => ValueTask.FromResult(true) };
    }

    /// <summary>実有効版の変更を観測しつつ、通常保存接続実装を使います。</summary>
    /// <returns>用途方針を接続したstoreです。</returns>
    private static WorkflowStore Store(IDurableRepository repository, PeerCluster cluster, AvailabilityService? availability, Dictionary<string, CompiledWorkflow> effective) => new(repository, cluster, new WorkflowCompiler(new NodeRegistry([])))
    {
        Availability = availability,
        GetEffective = id => effective.GetValueOrDefault(id),
        ActivateHandler = (workflow, token) =>
        {
            effective[workflow.Definition.Id] = workflow;
            return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
        },
    };
}
