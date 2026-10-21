using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>候補全体の転送と、固定候補に対する原子解消を検証します。</summary>
public sealed class ConflictReplicationTests
{
    /// <summary>第三Peerにも分岐を保持し、通常CASは拒否して明示解消だけを全側へ確定します。</summary>
    /// <returns>SQLiteとJSONで同じ保存契約の確認です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FullCandidateExportAndPreparedResolutionConverge(bool sqlite)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-fork-" + Guid.NewGuid().ToString("N"));
        IDurableRepository first = Create(root, "a", sqlite);
        IDurableRepository second = Create(root, "b", sqlite);
        IDurableRepository third = Create(root, "c", sqlite);
        DurableReceipt original = await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:demo", "key", null, NodeValue.FromObject(0))]);
        Assert.True(await second.ImportAsync(await first.ExportAsync()));
        await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:demo", "key", original.Entries[0].Version, NodeValue.FromObject(1))]);
        await second.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:demo", "key", original.Entries[0].Version, NodeValue.FromObject(2))]);
        Assert.False(await first.ImportAsync(await second.ExportAsync()));
        Assert.False(await second.ImportAsync(await first.ExportAsync()));
        Assert.False(await third.ImportAsync(await first.ExportAsync()));
        Assert.Equal(2, (await third.GetConflictsAsync(string.Empty)).Count);
        Assert.Equal(await first.GetSharedVersionAsync(), await second.GetSharedVersionAsync());
        DurableEntry current = (await third.ReadAsync("workflow:demo", "key"))!;
        Assert.Equal(EOutcome.Failed, (await third.ApplyBatchAsync(Guid.NewGuid(), [new(current.Scope, current.Key, current.Version, NodeValue.FromObject(3))])).Outcome);
        string[] versions = (await first.GetConflictsAsync(string.Empty)).Select(entry => entry.Version).Order(StringComparer.Ordinal).ToArray();
        Guid id = Guid.NewGuid();
        DurableMutation[] resolution = [new("workflow:demo", "key", null, NodeValue.FromObject(9), versions)];
        foreach (IDurableRepository repository in new[] { first, second, third })
        {
            Assert.Equal(EOutcome.Succeeded, (await repository.PrepareAsync(id, resolution)).Outcome);
            Assert.Equal(2, (await repository.GetConflictsAsync(string.Empty)).Count);
        }
        foreach (IDurableRepository repository in new[] { first, second, third })
        {
            Assert.Equal(EOutcome.Succeeded, (await repository.CommitAsync(id)).Outcome);
            Assert.Empty(await repository.GetConflictsAsync(string.Empty));
        }
        Assert.Equal(ContractJson.Hash((await first.ReadReceiptAsync(id))!.Entries), ContractJson.Hash((await second.ReadReceiptAsync(id))!.Entries));
        Assert.True(await third.ImportAsync(await first.ExportAsync()));
    }

    /// <summary>選択した保存バックエンドの独立領域を作ります。</summary>
    /// <returns>試験用保存先です。</returns>
    private static IDurableRepository Create(string root, string id, bool sqlite)
    {
        if (sqlite)
        {
            return new SqliteRepository(Path.Combine(root, id + ".db"));
        }
        return new JsonFileRepository(Path.Combine(root, id + ".json"));
    }
}
