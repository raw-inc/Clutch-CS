using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>二媒体の実保存で原子性・所有先・同期・再起動の境界を検証します。</summary>
public sealed class RoutingRepositoryTests
{
    /// <summary>定義・履歴・スナップショット保護を同媒体で一括確定します。</summary>
    /// <returns>保存結果と実媒体配置の検証です。</returns>
    [Fact]
    public async Task ConfigurationGroupRemainsAtomicAndRuntimeUsesOtherBackend()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", Options());
        Guid configurationId = Guid.NewGuid();
        DurableReceipt configuration = await repository.ApplyBatchAsync(configurationId, [new("workflows", "w", null, NodeValue.FromObject(1)), new("workflow.history:w", "r1", null, NodeValue.FromObject(1)), new("snapshot.edit-locks", "w", null, NodeValue.FromObject("guard"))]);
        Assert.Equal(EOutcome.Succeeded, configuration.Outcome);
        Assert.Equal(3, configuration.Entries.Count);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:w", "state", null, NodeValue.FromObject(42))]);
        SqliteRepository configurationStore = new(Path.Combine(files.Root, "state.db"));
        JsonFileRepository runtimeStore = new(Path.Combine(files.Root, "state.json"));
        Assert.NotNull(await configurationStore.ReadAsync("workflows", "w"));
        Assert.Null(await configurationStore.ReadAsync("workflow:w", "state"));
        Assert.NotNull(await runtimeStore.ReadAsync("workflow:w", "state"));
        DurableReceipt rejected = await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "w", "wrong", NodeValue.FromObject(2)), new("workflow.history:w", "r2", null, NodeValue.FromObject(2))]);
        Assert.Equal(EOutcome.Failed, rejected.Outcome);
        Assert.Null(await repository.ReadAsync("workflow.history:w", "r2"));
    }

    /// <summary>跨媒体一括処理は片側さえ変えず同operationIdの拒否を保持します。</summary>
    /// <returns>再起動後も拒否を失わない検証です。</returns>
    [Fact]
    public async Task CrossBackendBatchIsRejectedBeforeAnyMutationAndSurvivesRestart()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", Options());
        Guid id = Guid.NewGuid();
        DurableMutation[] batch = [new("workflows", "w", null, NodeValue.FromObject(1)), new("workflow:w", "state", null, NodeValue.FromObject(2))];
        Assert.Equal(EOutcome.NotExecuted, (await repository.ApplyBatchAsync(id, batch)).Outcome);
        Assert.Null(await repository.ReadAsync("workflows", "w"));
        Assert.Null(await repository.ReadAsync("workflow:w", "state"));
        IDurableRepository restarted = await RepositoryFactory.CreateAsync(files.Root, "a", Options());
        Assert.Equal("Persistence.CrossBackendBatch", (await restarted.ReadReceiptAsync(id))!.Reason);
        Assert.Equal(EOutcome.NotExecuted, (await restarted.PrepareAsync(id, batch)).Outcome);
        Assert.Equal("Persistence.OperationMismatch", (await restarted.ApplyBatchAsync(id, [batch[0]])).Reason);
    }

    /// <summary>prepare後の再起動でコミットとabortを別媒体へ誤配送しません。</summary>
    /// <returns>JSONに固定した二つの操作の検証です。</returns>
    [Fact]
    public async Task PreparedOwnerSurvivesRestartForCommitAndAbort()
    {
        using MediaTestFiles files = new();
        IDurableRepository first = await RepositoryFactory.CreateAsync(files.Root, "a", Options());
        Guid commitId = Guid.NewGuid();
        Guid abortId = Guid.NewGuid();
        Assert.Equal(EOutcome.Succeeded, (await first.PrepareAsync(commitId, [new("workflow:w", "commit", null, NodeValue.FromObject(42))])).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await first.PrepareAsync(abortId, [new("workflow:w", "abort", null, NodeValue.FromObject(99))])).Outcome);
        IDurableRepository restarted = await RepositoryFactory.CreateAsync(files.Root, "a", Options());
        Assert.True(await restarted.HasPendingWriteAsync("workflow:w", "commit"));
        Assert.True(await restarted.HasPendingWriteAsync("workflow:w", "abort"));
        DurableReceipt committed = await restarted.CommitAsync(commitId);
        Assert.False(await restarted.HasPendingWriteAsync("workflow:w", "commit"));
        Assert.Equal(EOutcome.Succeeded, committed.Outcome);
        Assert.Equal(42, (await restarted.ReadAsync("workflow:w", "commit"))!.Value!.Json.GetInt32());
        Assert.Equal("Persistence.OperationMismatch", (await restarted.ApplyBatchAsync(commitId, [new("workflows", "other", null, NodeValue.FromObject(1))])).Reason);
        Assert.Null(await restarted.ReadAsync("workflows", "other"));
        Assert.NotNull(await restarted.ReadPreparationAsync(abortId));
        Assert.Equal("Persistence.Aborted", (await restarted.AbortPreparedAsync(abortId)).Reason);
        Assert.Null(await restarted.ReadPreparationAsync(abortId));
        Assert.Null(await restarted.ReadPreparationAsync(commitId));
        Assert.False(await restarted.HasPendingWriteAsync("workflow:w", "abort"));
        Assert.Equal("Persistence.Aborted", (await restarted.CommitAsync(abortId)).Reason);
        Assert.Null(await restarted.ReadAsync("workflow:w", "abort"));
    }

    /// <summary>別Peerは送り元の物理媒体を強制されず空操作記録も照会できます。</summary>
    /// <returns>二Peerの同期と共通versionの検証です。</returns>
    [Fact]
    public async Task ReplicationUsesLocalPurposeMappingAndKeepsEmptyReceiptOwners()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles destinationFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "a", Options());
        PersistenceOptions reverse = new(EStorageBackend.Json, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Runtime] = EStorageBackend.Sqlite });
        IDurableRepository destination = await RepositoryFactory.CreateAsync(destinationFiles.Root, "b", reverse);
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "w", null, NodeValue.FromObject(1))]);
        Guid stateId = Guid.NewGuid();
        await source.ApplyBatchAsync(stateId, [new("workflow:w", "state", null, NodeValue.FromObject(42))]);
        Guid abortedId = Guid.NewGuid();
        await source.PrepareAsync(abortedId, [new("workflow:w", "aborted", null, NodeValue.FromObject(0))]);
        await source.AbortPreparedAsync(abortedId);
        Assert.True(await destination.ImportAsync(await source.ExportAsync()));
        Assert.Equal(await source.GetSharedVersionAsync(), await destination.GetSharedVersionAsync());
        Assert.Equal(EOutcome.Succeeded, (await destination.ReadReceiptAsync(stateId))!.Outcome);
        Assert.Equal("Persistence.Aborted", (await destination.ReadReceiptAsync(abortedId))!.Reason);
        IDurableRepository restarted = await RepositoryFactory.CreateAsync(destinationFiles.Root, "b", reverse);
        Assert.Equal("Persistence.Aborted", (await restarted.CommitAsync(abortedId)).Reason);
        Assert.Equal(42, (await new SqliteRepository(Path.Combine(destinationFiles.Root, "state.db")).ReadAsync("workflow:w", "state"))!.Value!.Json.GetInt32());
    }

    /// <summary>Import途中のI/O失敗後は部分値を公開せず固定内容から回復します。</summary>
    /// <returns>実ファイル障害の解消後に、同じ稼働実体で読取り・書込み・ハートビートが回復する検証です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InterruptedImportBlocksReadsUntilSameSnapshotRecovers(int resumeOperation)
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles destinationFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "a", Options());
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "w", null, NodeValue.FromObject(1))]);
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:w", "state", null, NodeValue.FromObject(42))]);
        IDurableRepository destination = await RepositoryFactory.CreateAsync(destinationFiles.Root, "b", Options());
        string blockedFile = Path.Combine(destinationFiles.Root, "state.json");
        Directory.CreateDirectory(blockedFile);
        RepositorySnapshot snapshot = await source.ExportAsync();
        Exception? failure = await Record.ExceptionAsync(() => destination.ImportAsync(snapshot).AsTask()).ConfigureAwait(true);
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.NotNull(await new SqliteRepository(Path.Combine(destinationFiles.Root, "state.db")).ReadAsync("workflows", "w"));
        Exception? stillBlocked = await Record.ExceptionAsync(() => destination.ReadAsync("workflows", "w").AsTask()).ConfigureAwait(true);
        Assert.True(stillBlocked is IOException or UnauthorizedAccessException);
        Directory.Delete(blockedFile);
        string stagedPath = Path.Combine(destinationFiles.Root, "routing-import.json");
        byte[] staged = await File.ReadAllBytesAsync(stagedPath).ConfigureAwait(true);
        await File.AppendAllTextAsync(stagedPath, "corrupt").ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => destination.ReadAsync("workflows", "w").AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => RepositoryFactory.CreateAsync(destinationFiles.Root, "b", Options())).ConfigureAwait(true);
        await File.WriteAllBytesAsync(stagedPath, staged).ConfigureAwait(true);
        if (resumeOperation == 1)
        {
            Assert.Equal(EOutcome.Succeeded, await destination.WriteLocalSnapshotAsync("peer:b:resources", "history", NodeValue.FromObject(7)));
        }
        if (resumeOperation == 2)
        {
            Assert.Equal(await source.GetSharedVersionAsync(), await destination.GetSharedVersionAsync());
        }
        Assert.Equal(42, (await destination.ReadAsync("workflow:w", "state"))!.Value!.Json.GetInt32());
        Assert.Equal(await source.GetSharedVersionAsync(), await destination.GetSharedVersionAsync());
        Assert.False(File.Exists(stagedPath));
        Assert.Equal(snapshot.Receipts.Count, (await destination.ExportAsync()).Receipts.Count);
    }

    /// <summary>旧単一媒体は互換を保ち、既存割当の変更は空保存へ切り替えません。</summary>
    /// <returns>旧保存物と設定不一致の検証です。</returns>
    [Fact]
    public async Task LegacySingleStoreIsPreservedAndMappingChangeIsRejected()
    {
        using MediaTestFiles files = new();
        SqliteRepository old = new(Path.Combine(files.Root, "state.db"));
        Guid id = Guid.NewGuid();
        await old.ApplyBatchAsync(id, [new("workflows", "old", null, NodeValue.FromObject(9))]);
        Guid preparedId = Guid.NewGuid();
        await old.PrepareAsync(preparedId, [new("workflow:w", "legacy-prepared", null, NodeValue.FromObject(7))]);
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", new PersistenceOptions());
        Assert.Equal(9, (await repository.ReadAsync("workflows", "old"))!.Value!.Json.GetInt32());
        Assert.Equal(EOutcome.Succeeded, (await repository.ReadReceiptAsync(id))!.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await repository.CommitAsync(preparedId)).Outcome);
        PersistenceOperationOwner owner = Assert.Single((await repository.ExportAsync()).OperationOwners!, item => item.OperationId == preparedId);
        Assert.Contains("workflow:w", owner.Scopes);
        Assert.Equal((await repository.ReadReceiptAsync(preparedId))!.Fingerprint, owner.Fingerprint);
        await Assert.ThrowsAsync<InvalidDataException>(() => RepositoryFactory.CreateAsync(files.Root, "a", Options())).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => RepositoryFactory.CreateAsync(files.Root, "a", new PersistenceOptions(EStorageBackend.Json))).ConfigureAwait(true);
    }

    /// <summary>定義はSQLite、runtimeだけJSONへ分けた設定です。</summary>
    /// <returns>試験の固定設定です。</returns>
    private static PersistenceOptions Options() => new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Runtime] = EStorageBackend.Json });
}
