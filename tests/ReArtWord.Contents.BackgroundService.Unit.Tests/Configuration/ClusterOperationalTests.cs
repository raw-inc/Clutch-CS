using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Snapshots;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>異なるPeer起動設定からも共有運用版とスナップショット境界を維持します。</summary>
public sealed class ClusterOperationalTests
{
    /// <summary>復元対象でないPeer経由の共通保存・反映・直接復元も共有保護を迂回できません。</summary>
    /// <returns>実UDPで共有する全体保護の検証の完了。</returns>
    [Fact]
    public async Task SnapshotProtectionRejectsCommonWritesFromAnotherPeer()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-operational-" + Guid.NewGuid().ToString("N"));
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        SqliteRepository firstRepository = new(Path.Combine(root, "a.db"));
        SqliteRepository secondRepository = new(Path.Combine(root, "b.db"));
        await using PeerCluster first = Create(id, "a", port, true, firstRepository);
        await first.StartAsync().ConfigureAwait(true);
        using ClusterConfigurationStore firstStore = new(firstRepository, first);
        ClusterOperationalSettings initial = FromToml(root, "UTC", "fail", "keep", "host-a");
        await firstStore.InitializeAsync(initial, true).ConfigureAwait(true);
        await using PeerCluster second = Create(id, "b", port, false, secondRepository);
        await second.StartAsync().ConfigureAwait(true);
        await WaitAsync(() => first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2).ConfigureAwait(true);
        using ClusterConfigurationStore secondStore = new(secondRepository, second);
        await secondStore.InitializeAsync(initial, false).ConfigureAwait(true);
        SnapshotService firstSnapshots = Snapshots(firstRepository, first, firstStore);
        SnapshotService secondSnapshots = Snapshots(secondRepository, second, secondStore);
        SnapshotTarget target = new(ClusterConfigurationStore.Id, "a", ESnapshotKind.Configuration);
        SnapshotCaptureResult capture = await firstSnapshots.CaptureAsync(Guid.NewGuid(), target).ConfigureAwait(true);
        ClusterOperationalSaveResult later = await firstStore.SaveAsync(new(Guid.NewGuid(), initial with { TimeZoneId = "Asia/Tokyo" }, firstStore.Capture()!.Revision, EActivationPolicy.Explicit)).ConfigureAwait(true);
        NodeValue laterState = await firstStore.SnapshotParticipant.CaptureAsync().ConfigureAwait(true);
        RuntimeParticipant participant = new("pause", 1);
        firstSnapshots.RegisterRuntime(participant);
        SnapshotTarget pause = new("pause", "a", ESnapshotKind.Runtime);
        SnapshotCaptureResult pauseCapture = await firstSnapshots.CaptureAsync(Guid.NewGuid(), pause).ConfigureAwait(true);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        participant.RestoreWait = release.Task;
        SnapshotRestoreRequest request = new(Guid.NewGuid(), [new(target, capture.Snapshot!.Id), new(pause, pauseCapture.Snapshot!.Id)]);
        Task<SnapshotRestoreResult> restoring = firstSnapshots.RestoreAsync(request);
        try
        {
            await WaitAsync(() => participant.Restores != 0).ConfigureAwait(true);
            Assert.True(Assert.Single(await secondSnapshots.GetEditLocksAsync().ConfigureAwait(true)).IsConfigurationParticipant);
            Assert.False(secondSnapshots.IsConfigurationLocked(ClusterConfigurationStore.Id));
            string revision = (await secondStore.GetAsync().ConfigureAwait(true)).Saved!.Revision;
            Assert.Equal(EOutcome.NotExecuted, (await secondStore.SaveAsync(new(Guid.NewGuid(), initial with { FileConflictPolicy = EFileConflictPolicy.Wait }, revision)).ConfigureAwait(true)).Saved.Outcome);
            Assert.Equal(EOutcome.NotExecuted, (await secondStore.ActivateAsync(new(Guid.NewGuid(), later.Revision!)).ConfigureAwait(true)).Outcome);
            Assert.Equal(EOutcome.NotExecuted, (await secondStore.RestoreSnapshotAsync(laterState).ConfigureAwait(true)).Outcome);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(EOutcome.Succeeded, (await restoring.ConfigureAwait(true)).Outcome);
        Assert.Empty(await secondSnapshots.GetEditLocksAsync().ConfigureAwait(true));
        string after = (await secondStore.GetAsync().ConfigureAwait(true)).Saved!.Revision;
        Assert.Equal(EOutcome.Succeeded, (await secondStore.SaveAsync(new(Guid.NewGuid(), initial with { TimeZoneId = "Asia/Tokyo" }, after)).ConfigureAwait(true)).Shared.Outcome);
    }

    /// <summary>DetachedでもReady判定より先に最後のローカル有効版を復元します。</summary>
    /// <returns>TOMLへ戻らない初期化境界の確認です。</returns>
    [Fact]
    public async Task DetachedInitializationLoadsLastEffectiveBeforeReadyCheck()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-operational-" + Guid.NewGuid().ToString("N"));
        FailingRepository repository = new(new SqliteRepository(Path.Combine(root, "a.db")));
        await using PeerCluster cluster = Create(Guid.NewGuid().ToString("N"), "a", Port(), true, repository);
        await cluster.StartAsync().ConfigureAwait(true);
        using ClusterConfigurationStore first = new(repository, cluster);
        ClusterOperationalSettings settings = FromToml(root, "UTC", "wait", "restore", "host-a");
        await first.InitializeAsync(settings, true).ConfigureAwait(true);
        string revision = first.Capture()!.Revision;
        await cluster.DetachAsync().ConfigureAwait(true);
        using ClusterConfigurationStore restarted = new(repository, cluster);
        ConfigurationPhaseResult result = await restarted.InitializeAsync(settings with { TimeZoneId = "Asia/Tokyo" }, false).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal(revision, restarted.Capture()!.Revision);
        Assert.Equal("UTC", restarted.Capture()!.Settings.TimeZoneId);
        Assert.Equal(EFileConflictPolicy.Wait, restarted.Capture()!.Settings.FileConflictPolicy);
    }

    /// <summary>後続PeerのTOMLが違っても共通版を使い、取得済み一件は旧版を維持します。</summary>
    /// <returns>実UDP共有の検証の完了。</returns>
    [Fact]
    public async Task DifferentPeerSeedsConvergeAndCapturedOccurrenceDoesNotChange()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-operational-" + Guid.NewGuid().ToString("N"));
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        FailingRepository firstRepository = new(new SqliteRepository(Path.Combine(root, "a.db")));
        FailingRepository secondRepository = new(new SqliteRepository(Path.Combine(root, "b.db")));
        await using PeerCluster first = Create(id, "a", port, true, firstRepository);
        await first.StartAsync().ConfigureAwait(true);
        using ClusterConfigurationStore firstStore = new(firstRepository, first);
        ClusterOperationalSettings initial = FromToml(root, "UTC", "fail", "keep", "host-a");
        Assert.Equal(EOutcome.Succeeded, (await firstStore.InitializeAsync(initial, true).ConfigureAwait(true)).Outcome);
        await using PeerCluster second = Create(id, "b", port, false, secondRepository);
        await second.StartAsync().ConfigureAwait(true);
        await WaitAsync(() => first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 2).ConfigureAwait(true);
        using ClusterConfigurationStore secondStore = new(secondRepository, second);
        ClusterOperationalSettings conflictingToml = FromToml(root, "Asia/Tokyo", "wait", "restore", "host-b");
        Assert.Equal(EOutcome.Succeeded, (await secondStore.InitializeAsync(conflictingToml, false).ConfigureAwait(true)).Outcome);
        Assert.Equal(ContractJson.Hash(firstStore.Capture()), ContractJson.Hash(secondStore.Capture()));
        ClusterOperationalSnapshot occurrence = secondStore.Capture()!;
        SnapshotService firstSnapshots = Snapshots(firstRepository, first, firstStore);
        SnapshotService secondSnapshots = Snapshots(secondRepository, second, secondStore);
        SnapshotTarget firstTarget = new(ClusterConfigurationStore.Id, "a", ESnapshotKind.Configuration);
        SnapshotTarget secondTarget = new(ClusterConfigurationStore.Id, "b", ESnapshotKind.Configuration);
        SnapshotCaptureResult firstCapture = await firstSnapshots.CaptureAsync(Guid.NewGuid(), firstTarget).ConfigureAwait(true);
        SnapshotCaptureResult secondCapture = await secondSnapshots.CaptureAsync(Guid.NewGuid(), secondTarget).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, firstCapture.Result.Outcome);
        Assert.Equal(EOutcome.Succeeded, secondCapture.Result.Outcome);
        ClusterOperationalSaveResult saved = await firstStore.SaveAsync(new(Guid.NewGuid(), conflictingToml, occurrence.Revision)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
        Assert.Equal(EOutcome.Succeeded, saved.Snapshot.Outcome);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await secondStore.ReconcileAsync().ConfigureAwait(true)).Outcome);
        Assert.Equal("UTC", occurrence.Settings.TimeZoneId);
        Assert.Equal(EFileConflictPolicy.Fail, occurrence.Settings.FileConflictPolicy);
        Assert.Equal("host-a", Assert.Single(occurrence.Settings.HostGroups["screens"]));
        Assert.Equal("Asia/Tokyo", secondStore.Capture()!.Settings.TimeZoneId);
        Assert.Equal(EFileInternalFailurePolicy.Restore, secondStore.Capture()!.Settings.FileInternalFailurePolicy);
        Assert.Equal("host-b", Assert.Single(secondStore.Capture()!.Settings.HostGroups["screens"]));
        Assert.Equal(EOutcome.Succeeded, (await secondStore.InitializeAsync(initial, false).ConfigureAwait(true)).Outcome);
        Assert.Equal(saved.Revision, secondStore.Capture()!.Revision);
        SnapshotRestoreResult restored = await firstSnapshots.RestoreAsync(new(Guid.NewGuid(), [new(firstTarget, firstCapture.Snapshot!.Id), new(secondTarget, secondCapture.Snapshot!.Id)])).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, restored.Outcome);
        Assert.Equal(occurrence.Revision, firstStore.Capture()!.Revision);
        Assert.Equal(occurrence.Revision, secondStore.Capture()!.Revision);
    }

    /// <summary>スナップショット保存不能でも本体保存を取り消さず、新版を自動適用しません。</summary>
    /// <returns>段階分離と再要求非再実行の検証の完了。</returns>
    [Fact]
    public async Task SnapshotFailurePreservesOldEffectiveAndExplicitCaptureAllowsActivation()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-operational-" + Guid.NewGuid().ToString("N"));
        FailingRepository repository = new(new SqliteRepository(Path.Combine(root, "a.db")));
        await using PeerCluster cluster = Create(Guid.NewGuid().ToString("N"), "a", Port(), true, repository);
        await cluster.StartAsync().ConfigureAwait(true);
        using ClusterConfigurationStore store = new(repository, cluster);
        ClusterOperationalSettings initial = FromToml(root, "UTC", "fail", "keep", "host-a");
        await store.InitializeAsync(initial, true).ConfigureAwait(true);
        string before = store.Capture()!.Revision;
        repository.FailScope = "snapshots";
        ClusterOperationalSaveRequest request = new(Guid.NewGuid(), initial with { TimeZoneId = "Asia/Tokyo" }, before);
        ClusterOperationalSaveResult saved = await store.SaveAsync(request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
        Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
        Assert.Equal(EOutcome.Failed, saved.Snapshot.Outcome);
        Assert.Equal(EOutcome.NotExecuted, saved.Activation.Outcome);
        await store.ReconcileAsync().ConfigureAwait(true);
        Assert.Equal(before, store.Capture()!.Revision);
        repository.FailScope = null;
        Assert.Equal(saved.Snapshot, (await store.SaveAsync(request).ConfigureAwait(true)).Snapshot);
        Assert.Equal(EOutcome.NotExecuted, (await store.ActivateAsync(new(Guid.NewGuid(), saved.Revision!)).ConfigureAwait(true)).Outcome);
        ClusterOperationalSnapshot claimed = (await store.GetAsync().ConfigureAwait(true)).Saved! with { Settings = initial };
        NodeValue mismatched = NodeValue.FromObject(new ClusterOperationalState(claimed, store.Capture()));
        Guid malformedId = Guid.NewGuid();
        await new SnapshotCatalog(repository, cluster).SaveAsync(new(malformedId.ToString("N"), ClusterConfigurationStore.Id, "a", ESnapshotKind.Configuration, DateTimeOffset.UtcNow, mismatched, ContractJson.Hash(mismatched)), malformedId).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, (await store.ActivateAsync(new(Guid.NewGuid(), saved.Revision!)).ConfigureAwait(true)).Outcome);
        SnapshotService snapshots = Snapshots(repository, cluster, store);
        SnapshotCaptureResult manual = await snapshots.CaptureAsync(Guid.NewGuid(), new(ClusterConfigurationStore.Id, "a", ESnapshotKind.Configuration)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, manual.Result.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await store.ActivateAsync(new(Guid.NewGuid(), saved.Revision!)).ConfigureAwait(true)).Outcome);
        Assert.Equal(saved.Revision, store.Capture()!.Revision);
    }

    /// <summary>共通設定をConfiguration種別で取得・復元し保存版と有効版を比較します。</summary>
    /// <returns>既存スナップショットサービスを通る実復元の確認です。</returns>
    [Fact]
    public async Task ConfigurationSnapshotRestoresBothSavedAndEffective()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-operational-" + Guid.NewGuid().ToString("N"));
        FailingRepository repository = new(new JsonFileRepository(Path.Combine(root, "a.json")));
        await using PeerCluster cluster = Create(Guid.NewGuid().ToString("N"), "a", Port(), true, repository);
        await cluster.StartAsync().ConfigureAwait(true);
        using ClusterConfigurationStore store = new(repository, cluster);
        ClusterOperationalSettings initial = FromToml(root, "UTC", "fail", "keep", "host-a");
        await store.InitializeAsync(initial, true).ConfigureAwait(true);
        NodeValue before = await store.SnapshotParticipant.CaptureAsync().ConfigureAwait(true);
        SnapshotService snapshots = Snapshots(repository, cluster, store);
        SnapshotTarget target = new(ClusterConfigurationStore.Id, "a", ESnapshotKind.Configuration);
        SnapshotCaptureResult captured = await snapshots.CaptureAsync(Guid.NewGuid(), target).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, captured.Result.Outcome);
        await store.SaveAsync(new(Guid.NewGuid(), initial with { FileConflictPolicy = EFileConflictPolicy.Wait }, store.Capture()!.Revision)).ConfigureAwait(true);
        SnapshotRestoreResult restored = await snapshots.RestoreAsync(new(Guid.NewGuid(), [new(target, captured.Snapshot!.Id)])).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, restored.Outcome);
        Assert.True(await store.SnapshotParticipant.MatchesAsync(before).ConfigureAwait(true));
        Assert.Equal(EFileConflictPolicy.Fail, store.Capture()!.Settings.FileConflictPolicy);
    }

    /// <summary>同じ内容版を再保存しても最新の影響確認方針を履歴で迂回しません。</summary>
    /// <returns>確認必須の明示適用契約です。</returns>
    [Fact]
    public async Task LatestConfirmationPolicyCannotBeBypassedByHistory()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-operational-" + Guid.NewGuid().ToString("N"));
        FailingRepository repository = new(new SqliteRepository(Path.Combine(root, "a.db")));
        await using PeerCluster cluster = Create(Guid.NewGuid().ToString("N"), "a", Port(), true, repository);
        await cluster.StartAsync().ConfigureAwait(true);
        using ClusterConfigurationStore store = new(repository, cluster);
        ClusterOperationalSettings settings = FromToml(root, "UTC", "fail", "keep", "host-a");
        await store.InitializeAsync(settings, true).ConfigureAwait(true);
        string revision = store.Capture()!.Revision;
        ClusterOperationalSaveRequest request = new(Guid.NewGuid(), settings, revision, EActivationPolicy.ConfirmImpact);
        ClusterOperationalSaveResult result = await store.SaveAsync(request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Snapshot.Outcome);
        Assert.Equal(EOutcome.NotExecuted, result.Activation.Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await store.ActivateAsync(new(Guid.NewGuid(), revision)).ConfigureAwait(true)).Outcome);
        ConfigurationImpactPreview impact = await store.InspectImpactAsync().ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await store.ActivateAsync(new(Guid.NewGuid(), revision, true, impact.Token)).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Failed, (await store.SaveAsync(request with { Settings = settings with { TimeZoneId = "Asia/Tokyo" } }).ConfigureAwait(true)).Saved.Outcome);
        NodeValue confirmedPolicy = await store.SnapshotParticipant.CaptureAsync().ConfigureAwait(true);
        await store.SaveAsync(new(Guid.NewGuid(), settings, revision, EActivationPolicy.Auto)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await store.RestoreSnapshotAsync(confirmedPolicy).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await store.ActivateAsync(new(Guid.NewGuid(), revision)).ConfigureAwait(true)).Outcome);
    }

    /// <summary>異なる起動TOMLの共通運用候補だけを取り出します。</summary>
    /// <returns>Peer固有rootを含まない候補です。</returns>
    private static ClusterOperationalSettings FromToml(string root, string zone, string conflict, string failure, string host)
    {
        string text = $"""
            [cluster]
            id = "test"
            peerId = "test"
            address = "127.0.0.1"
            broadcast = "127.255.255.255"
            port = 59000
            [storage]
            directory = "state"
            [time]
            zone = "{zone}"
            [files]
            roots = ["content"]
            conflictPolicy = "{conflict}"
            internalFailurePolicy = "{failure}"
            [groups]
            screens = ["{host}"]
            """;
        CommonConfiguration configuration = CommonConfigurationReader.Read(text, root);
        return new(configuration.TimeZone.Id, configuration.Files.ConflictPolicy, configuration.Files.InternalFailurePolicy, configuration.HostGroups);
    }

    /// <summary>実際のスナップショット登録と復元lockを接続します。</summary>
    /// <returns>共通スナップショットサービスです。</returns>
    private static SnapshotService Snapshots(IDurableRepository repository, PeerCluster cluster, ClusterConfigurationStore store)
    {
        SnapshotService snapshots = new(repository, cluster, new WorkflowStore(repository, cluster, new WorkflowCompiler(new NodeRegistry([]))));
        snapshots.RegisterConfiguration(store.SnapshotParticipant);
        store.IsRestoreLocked = () => snapshots.IsConfigurationLocked(ClusterConfigurationStore.Id);
        return snapshots;
    }

    /// <summary>専用UDPportと独立した保存先でPeerを構成します。</summary>
    /// <returns>実通信するPeerです。</returns>
    private static PeerCluster Create(string id, string peer, int port, bool fresh, IDurableRepository repository) => new(new ClusterOptions(id, peer, peer, "127.0.0.1", "127.255.255.255", port, 1, fresh, Path.GetTempPath()) { HeartbeatInterval = TimeSpan.FromMilliseconds(100), PeerTimeout = TimeSpan.FromSeconds(3) }, repository) { ReadinessCheck = token => ValueTask.FromResult(true) };

    /// <summary>試験用の構成だけで使用するUDPportを予約確認します。</summary>
    /// <returns>空きポートです。</returns>
    private static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>実同期の成立を有限時間待ちます。</summary>
    /// <returns>同期確認の完了です。</returns>
    private static async Task WaitAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token).ConfigureAwait(true);
        }
    }
}
