using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>準備の受理後に同期が交差しても、同じ操作の確定結果を分岐させません。</summary>
public sealed class PreparedCommitSynchronizationTests
{
    /// <summary>別の分岐を受け取った参加者は、受理済み本文を保持して確定失敗とは区別します。</summary>
    /// <returns>競合中の結果不明と、解消後の元結果への収束です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentImportDoesNotTurnAcceptedCommitIntoFailureAsync(bool sqlite)
    {
        using MediaTestFiles files = new();
        IDurableRepository first = Create(files.Root, "first", sqlite);
        IDurableRepository second = Create(files.Root, "second", sqlite);
        IDurableRepository competing = Create(files.Root, "competing", sqlite);
        DurableEntry parent = Assert.Single((await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", null, NodeValue.FromObject(0))])).Entries);
        Assert.True(await second.ImportAsync(await first.ExportAsync()));
        Assert.True(await competing.ImportAsync(await first.ExportAsync()));
        Guid operation = Guid.NewGuid();
        IReadOnlyList<DurableMutation> mutations = await first.StampAsync([new(parent.Scope, parent.Key, parent.Version, NodeValue.FromObject(1))]);
        Assert.Equal(EOutcome.Succeeded, (await first.PrepareAsync(operation, mutations)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await second.PrepareAsync(operation, mutations)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await competing.ApplyBatchAsync(Guid.NewGuid(), [new(parent.Scope, parent.Key, parent.Version, NodeValue.FromObject(2))])).Outcome);
        Assert.True(await first.ImportAsync(await competing.ReadCurrentStateAsync()));
        Assert.Equal(EOutcome.Succeeded, (await second.CommitAsync(operation)).Outcome);
        Assert.Equal(EOutcome.Unknown, (await first.CommitAsync(operation)).Outcome);
        Assert.Equal(PreparedCommit.ConflictReason, (await first.ReadReceiptAsync(operation))!.Reason);
        Assert.Null(await first.ReadPreparationAsync(operation));
        Assert.Equal(2, (await first.GetConflictsAsync(parent.Scope)).Count);
        Assert.False(await second.ImportAsync(await first.ExportAsync()));
        Assert.Equal(await first.GetSharedVersionAsync(), await second.GetSharedVersionAsync());
        string[] versions = (await first.GetConflictsAsync(parent.Scope)).Select(entry => entry.Version).Distinct().ToArray();
        Assert.Equal(EOutcome.Succeeded, (await first.ResolveConflictAsync(Guid.NewGuid(), parent.Scope, parent.Key, versions, NodeValue.FromObject(1))).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await first.CommitAsync(operation)).Outcome);
        Assert.Equal(ContractJson.Hash(await first.ReadReceiptAsync(operation)), ContractJson.Hash(await second.ReadReceiptAsync(operation)));
        Assert.True(await second.ImportAsync(await first.ExportAsync()));
    }

    /// <summary>現在状態だけが先に進んでも、旧操作の本文を現在値へ再適用しません。</summary>
    /// <returns>成功結果の補完と、後続の現在値を巻き戻さないことの確認です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportedDescendantPreservesAcceptedResultWithoutReapplyingValueAsync(bool sqlite)
    {
        using MediaTestFiles files = new();
        IDurableRepository first = Create(files.Root, "first", sqlite);
        IDurableRepository second = Create(files.Root, "second", sqlite);
        Guid operation = Guid.NewGuid();
        IReadOnlyList<DurableMutation> mutations = await first.StampAsync([new("workflow:test", "value", null, NodeValue.FromObject(1))]);
        Assert.Equal(EOutcome.Succeeded, (await first.PrepareAsync(operation, mutations)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await second.PrepareAsync(operation, mutations)).Outcome);
        DurableEntry committed = Assert.Single((await first.CommitAsync(operation)).Entries);
        DurableEntry successor = Assert.Single((await first.ApplyBatchAsync(Guid.NewGuid(), [new(committed.Scope, committed.Key, committed.Version, NodeValue.FromObject(2))])).Entries);
        Assert.True(await second.ImportAsync(await first.ReadCurrentStateAsync()));
        Assert.Equal(EOutcome.Succeeded, (await second.CommitAsync(operation)).Outcome);
        Assert.Equal(ContractJson.Hash(await first.ReadReceiptAsync(operation)), ContractJson.Hash(await second.ReadReceiptAsync(operation)));
        Assert.Equal(successor.Version, (await second.ReadAsync(committed.Scope, committed.Key))!.Version);
        Assert.Null(await second.ReadPreparationAsync(operation));
        Assert.Empty(await second.GetConflictsAsync(string.Empty));
    }

    /// <summary>各試験で独立した実保存媒体を作ります。</summary>
    /// <returns>JSONまたはSQLiteの永続保存先です。</returns>
    private static IDurableRepository Create(string root, string name, bool sqlite)
        => sqlite ? new SqliteRepository(Path.Combine(root, name + ".db"))
                  : new JsonFileRepository(Path.Combine(root, name + ".json"));
}
