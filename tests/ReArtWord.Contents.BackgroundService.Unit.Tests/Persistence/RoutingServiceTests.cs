using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>実サービスの正規一括処理が用途選択で壊れないことを実UDPで確認します。</summary>
public sealed class RoutingServiceTests
{
    /// <summary>異なる保存方式の二Peerで正規保存・スナップショット・復元・離脱ローカル保存を実行します。</summary>
    /// <returns>実UDPと保存媒体の検証です。</returns>
    [Fact]
    public async Task RealWorkflowStoreAndSnapshotsUseConfigurationRouteAcrossPeers()
    {
        using MediaTestFiles firstFiles = new();
        using MediaTestFiles secondFiles = new();
        PersistenceOptions firstOptions = new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Configuration] = EStorageBackend.Json });
        IDurableRepository firstRepository = await RepositoryFactory.CreateAsync(firstFiles.Root, "a", firstOptions);
        IDurableRepository secondRepository = await RepositoryFactory.CreateAsync(secondFiles.Root, "b", new PersistenceOptions());
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        await using PeerCluster first = Peer(id, "a", firstFiles.Root, port, true, firstRepository);
        await first.StartAsync().ConfigureAwait(true);
        WorkflowStore firstStore = Store(firstRepository, first);
        SnapshotService firstSnapshots = new(firstRepository, first, firstStore);
        WorkflowDefinition definition = new("routed", "before", [], []);
        WorkflowSaveResult initial = await firstStore.SaveAsync(new(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, initial.Activation.Outcome);
        await using PeerCluster second = Peer(id, "b", secondFiles.Root, port, false, secondRepository);
        WorkflowStore secondStore = Store(secondRepository, second);
        SnapshotService secondSnapshots = new(secondRepository, second, secondStore);
        Assert.NotNull(secondSnapshots);
        await second.StartAsync().ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (first.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2 || second.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(50, timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal(initial.Revision, (await secondStore.GetAsync(definition.Id).ConfigureAwait(true))!.Revision);
        SnapshotTarget target = new(definition.Id, "a", ESnapshotKind.Configuration);
        SnapshotCaptureResult captured = await firstSnapshots.CaptureAsync(Guid.NewGuid(), target).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, captured.Result.Outcome);
        WorkflowSaveResult changed = await firstStore.SaveAsync(new(Guid.NewGuid(), definition with { Name = "after" }, initial.Revision)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, changed.Saved.Outcome);
        Assert.Equal(EOutcome.Succeeded, changed.Shared.Outcome);
        Assert.Equal(EOutcome.Succeeded, changed.Snapshot.Outcome);
        Assert.Equal(EOutcome.Succeeded, changed.Activation.Outcome);
        Assert.Equal("after", (await secondStore.GetAsync(definition.Id).ConfigureAwait(true))!.Definition.Name);
        SnapshotRestoreResult restored = await firstSnapshots.RestoreAsync(new(Guid.NewGuid(), [new(target, captured.Snapshot!.Id)])).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, restored.Outcome);
        Assert.Equal("before", (await secondStore.GetAsync(definition.Id).ConfigureAwait(true))!.Definition.Name);
        JsonFileRepository configuration = new(Path.Combine(firstFiles.Root, "state.json"));
        Assert.NotNull(await configuration.ReadAsync("workflows", definition.Id));
        Assert.NotEmpty(await configuration.ListAsync("workflow.history:" + definition.Id));
        Assert.NotEmpty(await configuration.ListAsync("snapshots"));
        Assert.Null(await new SqliteRepository(Path.Combine(firstFiles.Root, "state.db")).ReadAsync("workflows", definition.Id));
        await first.DetachAsync().ConfigureAwait(true);
        string revision = (await firstStore.GetAsync(definition.Id).ConfigureAwait(true))!.Revision;
        WorkflowSaveResult detached = await firstStore.SaveAsync(new(Guid.NewGuid(), definition with { Name = "local" }, revision)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, detached.Saved.Outcome);
        Assert.Equal(EOutcome.NotExecuted, detached.Shared.Outcome);
        Assert.NotNull(await configuration.ReadAsync("peer:a:detached-workflows", definition.Id));
        Assert.NotEmpty(await configuration.ListAsync("peer:a:detached-snapshots"));
        Assert.Equal("before", (await secondStore.GetAsync(definition.Id).ConfigureAwait(true))!.Definition.Name);
    }

    /// <summary>ワークフローなしの実ResourceService履歴を選択したJSONへ保存します。</summary>
    /// <returns>実観測と再起動後の診断取得です。</returns>
    [Fact]
    public async Task ActualResourceHistoryUsesDiagnosticsBackend()
    {
        using MediaTestFiles files = new();
        PersistenceOptions options = new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Diagnostics] = EStorageBackend.Json });
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", options);
        using ResourceService resources = new(repository, "a", new ResourceOptions());
        ResourceSample captured = await resources.CaptureAsync();
        Assert.True(captured.HistoryStored);
        Assert.NotEmpty(captured.Metrics);
        Assert.NotNull(await new JsonFileRepository(Path.Combine(files.Root, "state.json")).ReadAsync("peer:a:resources", "history.index"));
        Assert.Null(await new JsonFileRepository(Path.Combine(files.Root, "state.json")).ReadAsync("peer:a:resources", "history"));
        Assert.Null(await new SqliteRepository(Path.Combine(files.Root, "state.db")).ReadAsync("peer:a:resources", "history.index"));
        IDurableRepository restarted = await RepositoryFactory.CreateAsync(files.Root, "a", options);
        using ResourceService second = new(restarted, "a", new ResourceOptions());
        Assert.Equal(captured.Id, Assert.Single(await second.HistoryAsync()).Id);
        Assert.Empty((await restarted.ExportAsync()).Entries);
    }

    /// <summary>実行計画だけを試験用の構成内に保持し、保存の本体を差し替えません。</summary>
    /// <returns>実WorkflowStoreです。</returns>
    private static WorkflowStore Store(IDurableRepository repository, PeerCluster cluster)
    {
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        return new(repository, cluster, new WorkflowCompiler(new NodeRegistry([])))
        {
            GetEffective = id => effective.GetValueOrDefault(id),
            ActivateHandler = (workflow, _) =>
            {
                effective[workflow.Definition.Id] = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
    }

    /// <summary>試験用の構成だけの実ブロードキャスト参加主体です。</summary>
    /// <returns>呼出側が終了するPeerです。</returns>
    private static PeerCluster Peer(string id, string peer, string root, int port, bool initialize, IDurableRepository repository) => new(new ClusterOptions(id, peer, "fixture-host", "127.0.0.1", "127.255.255.255", port, 1, initialize, root), repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };

    /// <summary>既存利用中のUDPportを選ばないようAnyで確認します。</summary>
    /// <returns>試験用の構成のポートです。</returns>
    private static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
