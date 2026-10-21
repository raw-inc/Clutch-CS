using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>用途分類と競合候補の保存を実媒体から確認します。</summary>
public sealed class RoutingIsolationTests
{
    /// <summary>誤ったPeer名での初回routing接続が既存所有者の次回起動を壊しません。</summary>
    /// <returns>拒否後も正しい所有者で接続できる検証です。</returns>
    [Fact]
    public async Task WrongPeerCannotSeedRoutingMarkerInLegacyStore()
    {
        using MediaTestFiles files = new();
        SqliteRepository legacy = new(Path.Combine(files.Root, "state.db"));
        PeerStorageIdentity identity = new("cluster", "a", "host", "fixture-interface", "127.0.0.1");
        await legacy.ApplyBatchAsync(Guid.NewGuid(), [new("peer:bootstrap", "identity", null, NodeValue.FromObject(identity))]);
        await Assert.ThrowsAsync<InvalidDataException>(() => RepositoryFactory.CreateAsync(files.Root, "b", new PersistenceOptions())).ConfigureAwait(true);
        Assert.Null(await legacy.ReadAsync("peer:persistence-routing", "configuration"));
        IDurableRepository correct = await RepositoryFactory.CreateAsync(files.Root, "a", new PersistenceOptions());
        Assert.NotNull(await correct.ReadAsync("peer:bootstrap", "identity"));
    }

    /// <summary>同媒体を選んだ用途間は従来どおり一括保存できます。</summary>
    /// <returns>一実体の原子取引を確認します。</returns>
    [Fact]
    public async Task SameBackendPurposesShareAtomicBatchAndLocalDiagnosticsStayLocal()
    {
        using MediaTestFiles files = new();
        PersistenceOptions options = new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Runtime] = EStorageBackend.Sqlite, [EStoragePurpose.Diagnostics] = EStorageBackend.Json, [EStoragePurpose.Processes] = EStorageBackend.Json });
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", options);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "w", null, NodeValue.FromObject(1)), new("workflow:w", "state", null, NodeValue.FromObject(42))])).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("peer:a:processDefinitions", "app", null, NodeValue.FromObject("definition")), new("peer:a:processStates", "app", null, NodeValue.FromObject("state"))])).Outcome);
        await repository.WriteLocalSnapshotAsync("peer:a:resources", "history", NodeValue.FromObject(123));
        JsonFileRepository json = new(Path.Combine(files.Root, "state.json"));
        Assert.NotNull(await json.ReadAsync("peer:a:resources", "history"));
        Assert.NotNull(await json.ReadAsync("peer:a:processStates", "app"));
        RepositorySnapshot shared = await repository.ExportAsync();
        Assert.Equal(2, shared.Entries.Count);
        Assert.Single(shared.Receipts);
        Assert.DoesNotContain(shared.Entries, entry => entry.Scope.StartsWith("peer:", StringComparison.Ordinal));
    }

    /// <summary>別Peerへ転送した未解決分岐もscope/keyだけで潰しません。</summary>
    /// <returns>第三Peerの候補保存と明示解消の検証です。</returns>
    [Fact]
    public async Task ForkCandidatesAndResolutionSurviveThirdPeerAndRestart()
    {
        using MediaTestFiles firstFiles = new();
        using MediaTestFiles secondFiles = new();
        using MediaTestFiles thirdFiles = new();
        PersistenceOptions options = new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Runtime] = EStorageBackend.Json });
        IDurableRepository first = await RepositoryFactory.CreateAsync(firstFiles.Root, "a", options);
        IDurableRepository second = await RepositoryFactory.CreateAsync(secondFiles.Root, "b", options);
        IDurableRepository third = await RepositoryFactory.CreateAsync(thirdFiles.Root, "c", options);
        DurableReceipt baseline = await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:w", "state", null, NodeValue.FromObject(0))]);
        Assert.True(await second.ImportAsync(await first.ExportAsync()));
        await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:w", "state", baseline.Entries[0].Version, NodeValue.FromObject(1))]);
        await second.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:w", "state", baseline.Entries[0].Version, NodeValue.FromObject(2))]);
        Assert.False(await first.ImportAsync(await second.ExportAsync()));
        Assert.False(await third.ImportAsync(await first.ExportAsync()));
        IDurableRepository restarted = await RepositoryFactory.CreateAsync(thirdFiles.Root, "c", options);
        IReadOnlyList<DurableEntry> candidates = await restarted.GetConflictsAsync("workflow:w");
        Assert.Equal(2, candidates.Count);
        Assert.Equal(await first.GetSharedVersionAsync(), await restarted.GetSharedVersionAsync());
        Guid resolutionId = Guid.NewGuid();
        DurableReceipt resolved = await restarted.ResolveConflictAsync(resolutionId, "workflow:w", "state", candidates.Select(entry => entry.Version).ToArray(), NodeValue.FromObject(3));
        Assert.Equal(EOutcome.Succeeded, resolved.Outcome);
        Assert.Empty(await restarted.GetConflictsAsync("workflow:w"));
        Assert.Equal(EOutcome.Succeeded, (await (await RepositoryFactory.CreateAsync(thirdFiles.Root, "c", options)).ReadReceiptAsync(resolutionId))!.Outcome);
    }

    /// <summary>送信元の一取引を受信側で複数媒体へ分割しません。</summary>
    /// <returns>設定差による開始前拒否を検証します。</returns>
    [Fact]
    public async Task IncomingAtomicBatchSplitAcrossBackendsIsRejectedBeforeImport()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles destinationFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "a", new PersistenceOptions());
        PersistenceOptions options = new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Runtime] = EStorageBackend.Json });
        IDurableRepository destination = await RepositoryFactory.CreateAsync(destinationFiles.Root, "b", options);
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "w", null, NodeValue.FromObject(1)), new("workflow:w", "state", null, NodeValue.FromObject(2))]);
        RepositorySnapshot snapshot = await source.ExportAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => destination.ImportAsync(snapshot).AsTask()).ConfigureAwait(true);
        Assert.Null(await destination.ReadAsync("workflows", "w"));
        Assert.Null(await destination.ReadAsync("workflow:w", "state"));
    }
}
