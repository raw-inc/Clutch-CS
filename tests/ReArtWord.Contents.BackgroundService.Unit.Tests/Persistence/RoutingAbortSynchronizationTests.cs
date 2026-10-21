using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>予約の到着前後で取消記録が異なる場合も、同期と再実行の禁止を維持します。</summary>
public sealed class RoutingAbortSynchronizationTests
{
    /// <summary>要求内容を知る側と知らない側の取消が、双方向の同期後も確定結果として残ります。</summary>
    /// <returns>媒体、再起動、遅延要求を含む取消維持の検証です。</returns>
    [Theory]
    [InlineData(EStorageBackend.Sqlite, EStorageBackend.Sqlite)]
    [InlineData(EStorageBackend.Sqlite, EStorageBackend.Json)]
    [InlineData(EStorageBackend.Json, EStorageBackend.Sqlite)]
    [InlineData(EStorageBackend.Json, EStorageBackend.Json)]
    public async Task AbortBeforeAndAfterPrepareSynchronizeWithoutReplacingLocalResults(EStorageBackend knownBackend, EStorageBackend earlyBackend)
    {
        using MediaTestFiles knownFiles = new();
        using MediaTestFiles earlyFiles = new();
        PersistenceOptions knownOptions = new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Runtime] = knownBackend });
        PersistenceOptions earlyOptions = new(earlyBackend);
        IDurableRepository known = await RepositoryFactory.CreateAsync(knownFiles.Root, "known", knownOptions);
        IDurableRepository early = await RepositoryFactory.CreateAsync(earlyFiles.Root, "early", earlyOptions);
        Guid id = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflow:cancel", "value", null, NodeValue.FromObject(42))];
        Assert.Equal(EOutcome.Succeeded, (await known.PrepareAsync(id, mutations)).Outcome);
        DurableReceipt knownAbort = await known.AbortPreparedAsync(id);
        DurableReceipt earlyAbort = await early.AbortPreparedAsync(id);
        Assert.NotEmpty(knownAbort.Fingerprint);
        Assert.Empty(earlyAbort.Fingerprint);
        await known.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "from-known", null, NodeValue.FromObject(1))]);
        await early.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "from-early", null, NodeValue.FromObject(2))]);

        Assert.True(await known.ImportAsync(await early.ExportAsync()));
        Assert.True(await early.ImportAsync(await known.ExportAsync()));
        Assert.Equal(await known.GetSharedVersionAsync(), await early.GetSharedVersionAsync());
        Assert.NotNull(await known.ReadAsync("workflows", "from-early"));
        Assert.NotNull(await early.ReadAsync("workflows", "from-known"));
        known = await RepositoryFactory.CreateAsync(knownFiles.Root, "known", knownOptions);
        early = await RepositoryFactory.CreateAsync(earlyFiles.Root, "early", earlyOptions);
        Assert.Equal(ContractJson.Hash(knownAbort), ContractJson.Hash(await known.ReadReceiptAsync(id)));
        Assert.Equal(ContractJson.Hash(earlyAbort), ContractJson.Hash(await early.ReadReceiptAsync(id)));
        foreach (IDurableRepository repository in new[] { known, early })
        {
            Assert.NotEqual(EOutcome.Succeeded, (await repository.PrepareAsync(id, mutations)).Outcome);
            Assert.NotEqual(EOutcome.Succeeded, (await repository.ApplyBatchAsync(id, mutations)).Outcome);
            Assert.Equal("Persistence.Aborted", (await repository.CommitAsync(id)).Reason);
            Assert.Null(await repository.ReadAsync("workflow:cancel", "value"));
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), mutations)).Outcome);
        }
    }

    /// <summary>異なる要求内容や適用済み結果を、指紋未取得の取消と同一視しません。</summary>
    /// <returns>実際の矛盾で取込み全体を拒否し、確定済みの結果を保持する検証です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ConflictingResultsStillRejectTheWholeSnapshot(bool committed, bool rejectedPrepare)
    {
        using MediaTestFiles leftFiles = new();
        using MediaTestFiles rightFiles = new();
        IDurableRepository left = await RepositoryFactory.CreateAsync(leftFiles.Root, "left", new());
        IDurableRepository right = await RepositoryFactory.CreateAsync(rightFiles.Root, "right", new());
        Guid id = Guid.NewGuid();
        await left.PrepareAsync(id, [new("workflow:cancel", "value", null, NodeValue.FromObject(1))]);
        if (committed)
        {
            await left.CommitAsync(id);
        }
        else
        {
            await left.AbortPreparedAsync(id);
            if (rejectedPrepare)
            {
                await right.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:cancel", "value", null, NodeValue.FromObject(10))]);
            }
            await right.PrepareAsync(id, [new("workflow:cancel", "value", null, NodeValue.FromObject(2))]);
        }
        await right.AbortPreparedAsync(id);
        await right.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "unrelated", null, NodeValue.FromObject(3))]);
        string previous = ContractJson.Hash(await left.ReadReceiptAsync(id));
        RepositorySnapshot incoming = await right.ExportAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => left.ImportAsync(incoming).AsTask());
        Assert.Equal(previous, ContractJson.Hash(await left.ReadReceiptAsync(id)));
        Assert.Null(await left.ReadAsync("workflows", "unrelated"));
        Assert.False(File.Exists(Path.Combine(leftFiles.Root, "routing-import.json")));
    }

    /// <summary>保存先の確保後にprepareが拒否された取消を、既知の所有先と照合して共有します。</summary>
    /// <returns>既存取消の維持、新規参加先の指紋補完、取消後の再実行禁止の検証です。</returns>
    [Theory]
    [InlineData(EStorageBackend.Sqlite)]
    [InlineData(EStorageBackend.Json)]
    public async Task RejectedPrepareKeepsItsOwnerWhenAbortHasNoFingerprint(EStorageBackend backend)
    {
        using MediaTestFiles rejectedFiles = new();
        using MediaTestFiles preparedFiles = new();
        using MediaTestFiles newFiles = new();
        IDurableRepository rejected = await RepositoryFactory.CreateAsync(rejectedFiles.Root, "rejected", new(backend));
        IDurableRepository prepared = await RepositoryFactory.CreateAsync(preparedFiles.Root, "prepared", new(backend));
        IDurableRepository newcomer = await RepositoryFactory.CreateAsync(newFiles.Root, "new", new(backend));
        await rejected.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:cancel", "value", null, NodeValue.FromObject(10))]);
        Guid id = Guid.NewGuid();
        DurableMutation[] mutations = [new("workflow:cancel", "value", null, NodeValue.FromObject(42))];
        Assert.Equal(EOutcome.Failed, (await rejected.PrepareAsync(id, mutations)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await prepared.PrepareAsync(id, mutations)).Outcome);
        DurableReceipt earlyAbort = await rejected.AbortPreparedAsync(id);
        DurableReceipt knownAbort = await prepared.AbortPreparedAsync(id);
        Assert.Empty(earlyAbort.Fingerprint);
        Assert.NotEmpty(knownAbort.Fingerprint);
        RepositorySnapshot snapshot = await rejected.ExportAsync();
        Assert.Equal(knownAbort.Fingerprint, snapshot.OperationOwners!.Single(owner => owner.OperationId == id).Fingerprint);
        Assert.True(await newcomer.ImportAsync(snapshot));
        Assert.Equal(ContractJson.Hash(knownAbort), ContractJson.Hash(await newcomer.ReadReceiptAsync(id)));
        Assert.True(await prepared.ImportAsync(snapshot));
        Assert.True(await rejected.ImportAsync(await prepared.ExportAsync()));
        Assert.Equal(ContractJson.Hash(earlyAbort), ContractJson.Hash(await rejected.ReadReceiptAsync(id)));
        Assert.Equal(ContractJson.Hash(knownAbort), ContractJson.Hash(await prepared.ReadReceiptAsync(id)));
        rejected = await RepositoryFactory.CreateAsync(rejectedFiles.Root, "rejected", new(backend));
        foreach (IDurableRepository repository in new[] { rejected, prepared, newcomer })
        {
            Assert.NotEqual(EOutcome.Succeeded, (await repository.PrepareAsync(id, mutations)).Outcome);
            Assert.NotEqual(EOutcome.Succeeded, (await repository.ApplyBatchAsync(id, mutations)).Outcome);
            Assert.Equal("Persistence.Aborted", (await repository.CommitAsync(id)).Reason);
            Assert.Equal(10, (await repository.ReadAsync("workflow:cancel", "value"))!.Value!.Json.GetInt32());
        }
    }
}
