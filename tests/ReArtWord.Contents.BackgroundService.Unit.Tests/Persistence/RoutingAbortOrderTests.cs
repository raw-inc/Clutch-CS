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

/// <summary>準備前、準備拒否後、準備成功後の取消を、送受信の全組み合わせで検証します。</summary>
public sealed class RoutingAbortOrderTests
{
    /// <summary>単一SQLiteと、用途によってJSONも使う構成で全到着順を試します。</summary>
    /// <returns>媒体と両側の取消時点です。</returns>
    public static IEnumerable<object[]> Cases()
    {
        foreach (EStorageBackend backend in new[] { EStorageBackend.Sqlite, EStorageBackend.Json })
        {
            foreach (string left in new[] { "before", "rejected", "prepared" })
            {
                foreach (string right in new[] { "before", "rejected", "prepared" })
                {
                    yield return [backend, left, right];
                }
            }
        }
    }

    /// <summary>指紋の所在が違っても、確定済みの取消と保存先を変更せず双方向に同期できます。</summary>
    /// <returns>再同期、再起動、遅延要求、次の新規操作を含む確認です。</returns>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AllAbortOrdersPreserveLocalResults(EStorageBackend backend, string leftStage, string rightStage)
    {
        using MediaTestFiles leftFiles = new();
        using MediaTestFiles rightFiles = new();
        PersistenceOptions options = new(EStorageBackend.Sqlite, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Runtime] = backend });
        IDurableRepository left = await RepositoryFactory.CreateAsync(leftFiles.Root, "left", options);
        IDurableRepository right = await RepositoryFactory.CreateAsync(rightFiles.Root, "right", options);
        Guid operation = Guid.NewGuid();
        Guid seed = Guid.NewGuid();
        DurableReceipt leftAbort = await CancelAsync(left, operation, seed, leftStage);
        DurableReceipt rightAbort = await CancelAsync(right, operation, seed, rightStage);
        await left.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "left", null, NodeValue.FromObject(1))]);
        await right.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "right", null, NodeValue.FromObject(2))]);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            Assert.True(await left.ImportAsync(await right.ExportAsync()));
            Assert.True(await right.ImportAsync(await left.ExportAsync()));
        }
        Assert.Equal(await left.GetSharedVersionAsync(), await right.GetSharedVersionAsync());
        Assert.NotNull(await left.ReadAsync("workflows", "right"));
        Assert.NotNull(await right.ReadAsync("workflows", "left"));
        left = await RepositoryFactory.CreateAsync(leftFiles.Root, "left", options);
        right = await RepositoryFactory.CreateAsync(rightFiles.Root, "right", options);
        Assert.Equal(ContractJson.Hash(leftAbort), ContractJson.Hash(await left.ReadReceiptAsync(operation)));
        Assert.Equal(ContractJson.Hash(rightAbort), ContractJson.Hash(await right.ReadReceiptAsync(operation)));
        foreach (IDurableRepository repository in new[] { left, right })
        {
            DurableMutation[] mutations = [new("workflow:cancel", "value", null, NodeValue.FromObject(42))];
            Assert.NotEqual(EOutcome.Succeeded, (await repository.PrepareAsync(operation, mutations)).Outcome);
            Assert.NotEqual(EOutcome.Succeeded, (await repository.ApplyBatchAsync(operation, mutations)).Outcome);
            Assert.Equal("Persistence.Aborted", (await repository.CommitAsync(operation)).Reason);
            DurableEntry? current = await repository.ReadAsync("workflow:cancel", "value");
            Assert.NotEqual(42, current?.Value?.Json.GetInt32());
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [mutations[0] with { ExpectedVersion = current?.Version }])).Outcome);
        }
    }

    /// <summary>所有先だけを根拠に、まだ準備中の操作を取消済みとして扱いません。</summary>
    /// <returns>未確定の準備と、本当に取消済みの状態を区別する確認です。</returns>
    [Theory]
    [InlineData(EStorageBackend.Sqlite)]
    [InlineData(EStorageBackend.Json)]
    public async Task OwnerWithoutCompletedAbortDoesNotSkipTheConflict(EStorageBackend backend)
    {
        using MediaTestFiles leftFiles = new();
        using MediaTestFiles rightFiles = new();
        IDurableRepository left = await RepositoryFactory.CreateAsync(leftFiles.Root, "left", new(backend));
        IDurableRepository right = await RepositoryFactory.CreateAsync(rightFiles.Root, "right", new(backend));
        Guid id = Guid.NewGuid();
        await left.PrepareAsync(id, [new("workflow:cancel", "value", null, NodeValue.FromObject(42))]);
        await right.AbortPreparedAsync(id);
        RepositorySnapshot incoming = await right.ExportAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => left.ImportAsync(incoming).AsTask());
        Assert.Null(await left.ReadReceiptAsync(id));
        Assert.Null(await left.ReadAsync("workflow:cancel", "value"));
        Assert.NotEmpty((await left.AbortPreparedAsync(id)).Fingerprint);
    }

    /// <summary>公開された保存操作だけで、各取消時点を作ります。</summary>
    /// <returns>そのPeerで確定した元の取消結果です。</returns>
    private static async Task<DurableReceipt> CancelAsync(IDurableRepository repository, Guid id, Guid seed, string stage)
    {
        string rejected = "rejected";
        string before = "before";
        if (string.Equals(stage, rejected, StringComparison.Ordinal))
        {
            await repository.ApplyBatchAsync(seed, [new("workflow:cancel", "value", null, NodeValue.FromObject(10))]).ConfigureAwait(false);
        }
        if (!string.Equals(stage, before, StringComparison.Ordinal))
        {
            DurableReceipt prepared = await repository.PrepareAsync(id, [new("workflow:cancel", "value", null, NodeValue.FromObject(42))]).ConfigureAwait(false);
            EOutcome expectedOutcome = EOutcome.Succeeded;
            if (string.Equals(stage, rejected, StringComparison.Ordinal))
            {
                expectedOutcome = EOutcome.Failed;
            }
            Assert.Equal(expectedOutcome, prepared.Outcome);
        }
        DurableReceipt result = await repository.AbortPreparedAsync(id).ConfigureAwait(false);
        string successful = "prepared";
        Assert.Equal(string.Equals(stage, successful, StringComparison.Ordinal), result.Fingerprint.Length != 0);
        return result;
    }
}
