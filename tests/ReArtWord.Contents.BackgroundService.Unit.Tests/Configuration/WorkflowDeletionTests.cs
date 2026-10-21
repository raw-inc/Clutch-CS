using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>削除前の復元候補と、共有削除・競合・再送の境界を確認します。</summary>
public sealed class WorkflowDeletionTests
{
    /// <summary>開始だけ記録された削除は結果不明として照会し、再送で進めません。</summary>
    /// <returns>中断境界の保存状態です。</returns>
    [Fact]
    public async Task InterruptedDeletionIsQueryableWithoutReexecution()
    {
        FailingRepository repository = Repository();
        await using PeerCluster cluster = Cluster(repository);
        await cluster.StartAsync();
        WorkflowStore store = Store(repository, cluster, new(StringComparer.Ordinal));
        WorkflowSaveResult saved = await store.SaveAsync(new(Guid.NewGuid(), new("demo", "demo", [], []), null));
        WorkflowDeleteRequest request = new(Guid.NewGuid(), "demo", saved.Revision!);
        ConfigurationOperationJournal journal = new(repository);
        Assert.True(await journal.BeginAsync("delete", request.OperationId, request, JsonSerializer.SerializeToElement(request)));
        Assert.Equal(EOutcome.Unknown, (await store.GetDeleteOperationAsync(request.OperationId))!.Deleted.Outcome);
        Assert.Equal(EOutcome.Unknown, (await store.DeleteAsync(request)).Deleted.Outcome);
        Assert.NotNull(await store.GetAsync("demo"));
    }

    /// <summary>削除印とスナップショットを維持し、履歴の旧版から再有効化しません。</summary>
    /// <returns>再送・再起動の確認です。</returns>
    [Fact]
    public async Task DeletionKeepsRecoverySnapshotAndCannotReplayOrReactivateHistory()
    {
        FailingRepository repository = Repository();
        await using PeerCluster cluster = Cluster(repository);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = Store(repository, cluster, effective);
        WorkflowSaveResult saved = await store.SaveAsync(new(Guid.NewGuid(), new("demo", "demo", [], []), null));
        WorkflowDeleteRequest request = new(Guid.NewGuid(), "demo", saved.Revision!);
        WorkflowDeleteResult result = await store.DeleteAsync(request);
        Assert.Equal(EOutcome.Succeeded, result.Snapshot.Outcome);
        Assert.Equal(EOutcome.Succeeded, result.Deleted.Outcome);
        Assert.Equal(EOutcome.Succeeded, result.Deactivation.Outcome);
        Assert.Null(await store.GetAsync("demo"));
        Assert.Empty(await store.ListAsync());
        Assert.Empty(effective);
        SnapshotRecord snapshot = (await repository.ReadAsync("snapshots", result.SnapshotId!))!.Value!.Json.Deserialize<SnapshotRecord>()!;
        ConfigurationSnapshotState original = snapshot.Value.Json.Deserialize<ConfigurationSnapshotState>()!;
        Assert.Equal("demo", original.Saved.Definition.Name);
        Assert.Equal(saved.Revision, original.Saved.Revision);
        Assert.Equal(result, await store.DeleteAsync(request));
        Assert.Equal(result, await store.GetDeleteOperationAsync(request.OperationId));
        Assert.Equal(EOutcome.NotExecuted, (await store.ActivateAsync(new(Guid.NewGuid(), "demo", saved.Revision!))).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await store.SaveAsync(new(Guid.NewGuid(), original.Saved.Definition, saved.Revision))).Activation.Outcome);
        Assert.Equal(EOutcome.Failed, (await store.DeleteAsync(request with { ExpectedRevision = "changed" })).Deleted.Outcome);
        WorkflowStore restarted = Store(repository, cluster, effective);
        await restarted.RestoreEffectiveAsync();
        await restarted.PrepareLocalAsync();
        Assert.Empty(effective);
        Assert.NotNull((await repository.ReadAsync("workflows", "demo"))!.Version);
        Assert.Null((await repository.ReadAsync("workflows", "demo"))!.Value);

        // 削除前データの再取込は新規保存として明示操作します。削除操作の再送とは分離します。
        WorkflowSaveResult restored = await restarted.SaveAsync(new(Guid.NewGuid(), original.Saved.Definition, null));
        Assert.Equal(EOutcome.Succeeded, restored.Activation.Outcome);
        Assert.Equal(saved.Revision, (await restarted.GetAsync("demo"))!.Revision);
    }

    /// <summary>古い確認版と復元候補の保存失敗はいずれも元定義を維持します。</summary>
    /// <returns>失敗時の状態確認です。</returns>
    [Theory]
    [InlineData("conflict")]
    [InlineData("snapshot")]
    [InlineData("storage")]
    public async Task FailedProtectionOrDeletionKeepsTheOriginal(string failure)
    {
        FailingRepository repository = Repository();
        await using PeerCluster cluster = Cluster(repository);
        await cluster.StartAsync();
        Dictionary<string, CompiledWorkflow> effective = new(StringComparer.Ordinal);
        WorkflowStore store = Store(repository, cluster, effective);
        WorkflowSaveResult saved = await store.SaveAsync(new(Guid.NewGuid(), new("demo", "demo", [], []), null));
        string revision = saved.Revision!;
        switch (failure)
        {
            case "conflict": revision = "older"; break;
            case "snapshot": repository.FailScope = "snapshots"; break;
            case "storage": repository.FailScope = "workflows"; break;
        }
        WorkflowDeleteRequest request = new(Guid.NewGuid(), "demo", revision);
        WorkflowDeleteResult result = await store.DeleteAsync(request);
        Assert.NotEqual(EOutcome.Succeeded, result.Deleted.Outcome);
        Assert.Equal(saved.Revision, (await store.GetAsync("demo"))!.Revision);
        Assert.Equal(saved.Revision, effective["demo"].Revision);
        repository.FailScope = null;
        Assert.Equal(result, await store.DeleteAsync(request));
        Assert.NotNull(await store.GetAsync("demo"));
    }

    /// <summary>独立保存先です。</summary>
    /// <returns>失敗を注入できるSQLiteです。</returns>
    private static FailingRepository Repository() => new(new SqliteRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db")));

    /// <summary>明示初期化したPeerです。</summary>
    /// <returns>試験専用クラスタです。</returns>
    private static PeerCluster Cluster(IDurableRepository repository)
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        return new(new ClusterOptions(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, Path.GetTempPath()), repository)
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
    }

    /// <summary>新規入力の有効版を独立に所有します。</summary>
    /// <returns>保存と適用を備えたストアです。</returns>
    private static WorkflowStore Store(IDurableRepository repository, PeerCluster cluster, Dictionary<string, CompiledWorkflow> effective)
        => new(repository, cluster, new WorkflowCompiler(new NodeRegistry([])))
        {
            GetEffective = id => effective.GetValueOrDefault(id),
            ActivateHandler = (workflow, _) =>
            {
                effective[workflow.Definition.Id] = workflow;
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
            DeactivateHandler = (id, _) =>
            {
                effective.Remove(id);
                return Task.FromResult(new ConfigurationPhaseResult(EOutcome.Succeeded));
            },
        };
}
