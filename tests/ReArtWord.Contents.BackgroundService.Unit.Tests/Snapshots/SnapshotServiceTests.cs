using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Snapshots;

/// <summary>自然欠損と意図的除外の補償境界を検証します。</summary>
public sealed class SnapshotServiceTests
{
    /// <summary>取得・復元の実操作だけを通知し、同じ操作の再送や照会で通知を増やしません。</summary>
    /// <returns>実保存・復元と共通Hookの確認。</returns>
    [Fact]
    public async Task SnapshotHooksDescribeActualOperationsWithoutReplayingOnQueriesAsync()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        PeerCluster cluster = CreateCluster(repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clusterLifetime = cluster.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await cluster.StartAsync(timeout.Token).ConfigureAwait(true);
        WorkflowEventHub events = new("one", cluster.LocalInstanceId);
        SnapshotService service = new(repository, cluster, new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([])))) { Events = events };
        RuntimeParticipant participant = new("A", 1);
        service.RegisterRuntime(participant);
        IAsyncEnumerator<WorkflowEvent> reader = events.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable readerLifetime = reader.ConfigureAwait(true);
        Task<bool> waiting = reader.MoveNextAsync().AsTask();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable waitingLifetime = new TestTaskLifetime(waiting, timeout).ConfigureAwait(true);
        Guid captureId = Guid.NewGuid();
        SnapshotTarget target = new("A", "one", ESnapshotKind.Runtime);
        SnapshotCaptureResult captured = await service.CaptureAsync(captureId, target, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, captured.Result.Outcome);
        Assert.True(await waiting.ConfigureAwait(true));
        Assert.Equal("snapshot.capture.started", reader.Current.Name);
        Assert.Equal(captureId, reader.Current.CausationId);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal("snapshot.capture.completed", reader.Current.Name);
        _ = await service.CaptureAsync(captureId, target, timeout.Token).ConfigureAwait(true);
        _ = await service.GetCaptureOperationAsync(captureId, timeout.Token).ConfigureAwait(true);
        Assert.Equal(2, events.Statistics.Published);
        participant.Current = 2;
        SnapshotRestoreRequest restore = new(Guid.NewGuid(), [new(target, captured.Snapshot!.Id)]);
        SnapshotRestoreResult restored = await service.RestoreAsync(restore, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, restored.Outcome);
        Assert.Equal(1, participant.Current);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal("snapshot.restore.started", reader.Current.Name);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal("snapshot.restore.completed", reader.Current.Name);
        Assert.Equal(restore.OperationId, reader.Current.CausationId);
        _ = await service.GetRestoreOperationAsync(restore.OperationId, timeout.Token).ConfigureAwait(true);
        _ = await service.RestoreAsync(restore, timeout.Token).ConfigureAwait(true);
        Assert.Equal(4, events.Statistics.Published);
    }

    /// <summary>元操作がない回復要求から新しい復元を開始しません。</summary>
    /// <returns>未変更と操作履歴非作成の検証完了。</returns>
    [Fact]
    public async Task RecoverWithoutOriginalOperationDoesNotStartRestoration()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        SnapshotService service = new(repository, cluster, new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))));
        RuntimeParticipant participant = new("A", 1);
        service.RegisterRuntime(participant);
        SnapshotTarget target = new("A", "one", ESnapshotKind.Runtime);
        SnapshotCaptureResult snapshot = await service.CaptureAsync(Guid.NewGuid(), target);
        participant.Current = 2;
        SnapshotRestoreRequest request = new(Guid.NewGuid(), [new SnapshotSelection(target, snapshot.Snapshot!.Id)]);
        SnapshotRestoreResult result = await service.RecoverAsync(request);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("Snapshot.OriginalOperationMissing", result.Reason);
        Assert.Equal(2, participant.Current);
        Assert.Equal(0, participant.Restores);
        Assert.Null(await service.GetRestoreOperationAsync(request.OperationId));
    }

    /// <summary>取得再送は時刻と内容を変えず別対象への同ID使用を拒否します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task CaptureReplayKeepsOriginalValueAndRejectsDifferentTarget()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        SnapshotService service = new(repository, cluster, new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))));
        RuntimeParticipant participant = new("A", 1);
        service.RegisterRuntime(participant);
        Guid operationId = Guid.NewGuid();
        SnapshotTarget target = new("A", "one", ESnapshotKind.Runtime);
        SnapshotCaptureResult first = await service.CaptureAsync(operationId, target);
        participant.Current = 2;
        SnapshotCaptureResult replay = await service.CaptureAsync(operationId, target);
        Assert.Equal(EOutcome.Succeeded, replay.Result.Outcome);
        Assert.Equal(first.Snapshot!.Hash, replay.Snapshot!.Hash);
        Assert.Equal(first.Snapshot.CapturedAt, replay.Snapshot.CapturedAt);
        Assert.Equal(EOutcome.Failed, (await service.CaptureAsync(operationId, target with { Id = "B" })).Result.Outcome);
    }

    /// <summary>自然欠損Bも補償し、意図的除外Bは補償しません。</summary>
    /// <returns>検証完了。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompensationRespectsOriginalScope(bool excludeB)
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        WorkflowStore workflows = new(repository, cluster, new WorkflowCompiler(new NodeRegistry([])));
        SnapshotService service = new(repository, cluster, workflows);
        RuntimeParticipant a = new("A", 1);
        RuntimeParticipant b = new("B", 2);
        RuntimeParticipant c = new("C", 3);
        service.RegisterRuntime(a);
        service.RegisterRuntime(b);
        service.RegisterRuntime(c);
        SnapshotTarget targetA = new("A", "one", ESnapshotKind.Runtime);
        SnapshotTarget targetB = new("B", "one", ESnapshotKind.Runtime);
        SnapshotTarget targetC = new("C", "one", ESnapshotKind.Runtime);
        Assert.Equal(EOutcome.Succeeded, (await service.CaptureAsync(Guid.NewGuid(), targetA)).Result.Outcome);
        SnapshotCaptureResult capturedC = await service.CaptureAsync(Guid.NewGuid(), targetC);
        Assert.Equal(EOutcome.Succeeded, capturedC.Result.Outcome);
        DateTimeOffset at = capturedC.Snapshot!.CapturedAt;
        a.Current = 10;
        b.Current = 20;
        c.Current = 30;
        c.FailValue = 3;
        c.OnFailure = () => b.Current = 99;
        SnapshotRestoreRequest request = new(Guid.NewGuid(), [new(targetA), new(targetB, Excluded: excludeB), new(targetC)], at);
        SnapshotRestoreResult result = await service.RestoreAsync(request);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(10, a.Current);
        Assert.Equal(30, c.Current);
        if (excludeB)
        {
            Assert.Equal(99, b.Current);
            Assert.Equal(0, b.Restores);
        }
        else
        {
            Assert.Equal(20, b.Current);
            Assert.Equal(1, b.Restores);
            Assert.Equal("Snapshot.NoCandidateAtTime", result.Targets.Single(target => string.Equals(target.Target.Id, "B", StringComparison.Ordinal)).Warning);
        }
        Assert.All(result.Targets.Where(target => !string.Equals(target.Target.Id, "B", StringComparison.Ordinal)), target => Assert.Equal(EOutcome.Succeeded, target.Compensation.Outcome));
    }

    /// <summary>指定済み欠損は別スナップショットへ差し替えず変更前に拒否します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task MissingExplicitSelectionDoesNotApplyAnything()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        SnapshotService service = new(repository, cluster, new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))));
        RuntimeParticipant participant = new("A", 10);
        service.RegisterRuntime(participant);
        SnapshotRestoreResult result = await service.RestoreAsync(new SnapshotRestoreRequest(Guid.NewGuid(), [new(new SnapshotTarget("A", "one", ESnapshotKind.Runtime), "missing")]));
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal(0, participant.Restores);
        Assert.Equal(10, participant.Current);
    }

    /// <summary>設定補償では保存中draftと実effectiveの違いを維持します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task ConfigurationCompensationRestoresSavedAndEffectiveIndependently()
    {
        SqliteRepository repository = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"));
        await using PeerCluster cluster = CreateCluster(repository);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = new(repository, cluster, new WorkflowCompiler(new NodeRegistry([])))
        {
            GetEffective = id => effective.GetValueOrDefault(id),
            ActivateHandler = (workflow, token) =>
            {
                effective[workflow.Definition.Id] = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
        SnapshotService service = new(repository, cluster, store);
        WorkflowDefinition definition = new("demo", "live", [], []);
        WorkflowSaveResult live = await store.SaveAsync(new WorkflowSaveRequest(Guid.NewGuid(), definition, null));
        SnapshotTarget configuration = new("demo", "one", ESnapshotKind.Configuration);
        SnapshotCaptureResult captured = await service.CaptureAsync(Guid.NewGuid(), configuration);
        await store.SaveAsync(new WorkflowSaveRequest(Guid.NewGuid(), definition with { Name = "draft" }, live.Revision, EActivationPolicy.Explicit));
        RuntimeParticipant failing = new("C", 3);
        service.RegisterRuntime(failing);
        SnapshotTarget runtime = new("C", "one", ESnapshotKind.Runtime);
        SnapshotCaptureResult runtimeCapture = await service.CaptureAsync(Guid.NewGuid(), runtime);
        failing.Current = 30;
        failing.FailValue = 3;
        SnapshotRestoreResult result = await service.RestoreAsync(new SnapshotRestoreRequest(Guid.NewGuid(), [new(configuration, captured.Snapshot!.Id), new(runtime, runtimeCapture.Snapshot!.Id)]));
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("draft", (await store.GetAsync("demo"))!.Definition.Name);
        Assert.Equal(EActivationPolicy.Explicit, (await store.GetAsync("demo"))!.ActivationPolicy);
        Assert.Equal("live", effective["demo"].Definition.Name);
        Assert.All(result.Targets, target => Assert.Equal(EOutcome.Succeeded, target.Compensation.Outcome));
    }

    /// <summary>遠隔適用の結果不明は元要求照会で回復し、再適用しません。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task UnknownRemoteApplyCanBeExplicitlyRecoveredWithoutReexecution()
    {
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), id);
        SqliteRepository firstRepository = new(Path.Combine(path, "first.db"));
        SqliteRepository secondRepository = new(Path.Combine(path, "second.db"));
        ClusterOptions firstOptions = new(id, "first", "host", "127.0.0.1", "127.255.255.255", port, 1, true, path)
        {
            RequestTimeout = TimeSpan.FromMilliseconds(700),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        };
        PeerCluster first = new(firstOptions, firstRepository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        PeerCluster second = new(firstOptions with { PeerId = "second", NewCluster = false }, secondRepository) { ReadinessCheck = token => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        SnapshotService firstService = new(firstRepository, first, new WorkflowStore(firstRepository, first, new WorkflowCompiler(new NodeRegistry([]))));
        SnapshotService secondService = new(secondRepository, second, new WorkflowStore(secondRepository, second, new WorkflowCompiler(new NodeRegistry([]))));
        RuntimeParticipant participant = new("state", 1);
        secondService.RegisterRuntime(participant);
        await first.StartAsync();
        await second.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(50, timeout.Token);
        }
        SnapshotTarget target = new("state", "second", ESnapshotKind.Runtime);
        SnapshotCaptureResult snapshot = await firstService.CaptureAsync(Guid.NewGuid(), target);
        Assert.Equal(EOutcome.Succeeded, snapshot.Result.Outcome);
        participant.Current = 10;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        participant.RestoreWait = release.Task;
        SnapshotRestoreRequest request = new(Guid.NewGuid(), [new(target, snapshot.Snapshot!.Id)]);
        SnapshotRestoreResult unknown = await firstService.RestoreAsync(request);
        Assert.Equal(EOutcome.Unknown, unknown.Outcome);
        release.SetResult();
        Assert.Equal(EOutcome.Unknown, (await firstService.RestoreAsync(request)).Outcome);
        SnapshotRestoreResult recovered = await firstService.RecoverAsync(request);
        Assert.Equal(EOutcome.Succeeded, recovered.Outcome);
        Assert.Equal(1, participant.Restores);
        Assert.Equal(1, participant.Current);
    }

    /// <summary>独立した明示初期化Peerを作ります。</summary>
    /// <returns>検証用Peer。</returns>
    private static PeerCluster CreateCluster(IDurableRepository repository)
    {
        using UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        return new PeerCluster(new ClusterOptions(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, Path.GetTempPath()), repository)
        {
            ReadinessCheck = token => ValueTask.FromResult(true),
        };
    }
}
