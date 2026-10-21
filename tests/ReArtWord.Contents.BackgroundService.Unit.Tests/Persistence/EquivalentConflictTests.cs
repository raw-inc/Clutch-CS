using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>同じ内容の分岐を安全に畳み、異なる内容や後続の更新を失わないことを検証します。</summary>
public sealed class EquivalentConflictTests
{
    /// <summary>三つの同値分岐を逆順に受けても、元結果を保った同じ版へ収束します。</summary>
    /// <returns>受信順、再起動、過去候補の再配送に依存しない統合です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EqualBranchesConvergeRegardlessOfOrderAndRestartAsync(bool sqlite)
    {
        using MediaTestFiles files = new();
        IDurableRepository first = Create(files.Root, "first", sqlite);
        IDurableRepository second = Create(files.Root, "second", sqlite);
        IDurableRepository third = Create(files.Root, "third", sqlite);
        List<RepositorySnapshot> branches = [];
        foreach (IDurableRepository repository in new[] { first, second, third })
        {
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("file:reservations", "active", null, NodeValue.FromObject(new Dictionary<string, string>()))])).Outcome);
            branches.Add(await repository.ExportAsync());
        }
        foreach (RepositorySnapshot branch in branches)
        {
            Assert.True(await first.ImportAsync(branch));
        }
        foreach (RepositorySnapshot branch in branches.AsEnumerable().Reverse())
        {
            Assert.True(await second.ImportAsync(branch));
        }
        Assert.Equal(await first.GetSharedVersionAsync(), await second.GetSharedVersionAsync());
        string expected = await first.GetSharedVersionAsync();
        first = Create(files.Root, "first", sqlite);
        foreach (RepositorySnapshot branch in branches)
        {
            Assert.True(await first.ImportAsync(branch));
        }
        Assert.Equal(expected, await first.GetSharedVersionAsync());
        Assert.Empty(await first.GetConflictsAsync(string.Empty));
        foreach (RepositorySnapshot branch in branches)
        {
            Assert.Equal(ContractJson.Hash(Assert.Single(branch.Receipts)), ContractJson.Hash(await first.ReadReceiptAsync(branch.Receipts[0].OperationId)));
        }
    }

    /// <summary>同値の一部だけを勝手に選ばず、異なる値を含む競合は残します。</summary>
    /// <returns>内容が異なる候補を自動選択しないことの確認です。</returns>
    [Fact]
    public async Task DifferentValuesRemainConflictedAsync()
    {
        using MediaTestFiles files = new();
        IDurableRepository first = Create(files.Root, "first", true);
        IDurableRepository second = Create(files.Root, "second", true);
        await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", null, NodeValue.FromObject(1))]);
        await second.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", null, NodeValue.FromObject(2))]);
        Assert.False(await first.ImportAsync(await second.ExportAsync()));
        Assert.Equal(2, (await first.GetConflictsAsync(string.Empty)).Count);
    }

    /// <summary>別の現役分岐を保持するPeerでも、既に統合された古い候補を競合へ残しません。</summary>
    /// <returns>遅れて参加した第三Peerの候補集合が一致し、次の明示解消を妨げないことです。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupersededConflictBranchesArePrunedBesideAnIndependentHeadAsync(bool sqlite)
    {
        using MediaTestFiles files = new();
        IDurableRepository first = Create(files.Root, "first", sqlite), second = Create(files.Root, "second", sqlite), third = Create(files.Root, "third", sqlite);
        DurableEntry parent = Assert.Single((await first.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", null, NodeValue.FromObject(0))])).Entries);
        Assert.True(await second.ImportAsync(await first.ExportAsync()));
        Assert.True(await third.ImportAsync(await first.ExportAsync()));
        int value = 0;
        foreach (IDurableRepository store in new[] { first, second, third })
        {
            await store.ApplyBatchAsync(Guid.NewGuid(), [new(parent.Scope, parent.Key, parent.Version, NodeValue.FromObject(++value))]);
        }
        RepositorySnapshot oldFirst = await first.ExportAsync(), oldSecond = await second.ExportAsync();
        Assert.False(await first.ImportAsync(oldSecond));
        string[] versions = (await first.GetConflictsAsync(parent.Scope)).Select(entry => entry.Version).Distinct().ToArray();
        Assert.Equal(EOutcome.Succeeded, (await first.ResolveConflictAsync(Guid.NewGuid(), parent.Scope, parent.Key, versions, NodeValue.FromObject(1))).Outcome);
        Assert.False(await third.ImportAsync(oldFirst));
        Assert.False(await third.ImportAsync(oldSecond));
        Assert.Equal(3, (await third.GetConflictsAsync(parent.Scope)).Count);
        Assert.False(await third.ImportAsync(await first.ExportAsync()));
        Assert.Equal(2, (await third.GetConflictsAsync(parent.Scope)).Count);
        Assert.False(await first.ImportAsync(await third.ExportAsync()));
        Assert.Equal(await first.GetSharedVersionAsync(), await third.GetSharedVersionAsync());
        Assert.False(await third.ImportAsync(oldSecond));
        Assert.Equal(2, (await third.GetConflictsAsync(parent.Scope)).Count);
    }

    /// <summary>試験ごとに独立し、同じパスで再起動可能な保存媒体を作ります。</summary>
    /// <returns>実ファイルへ保存するリポジトリです。</returns>
    private static IDurableRepository Create(string root, string name, bool sqlite)
        => sqlite ? new SqliteRepository(Path.Combine(root, name + ".db"))
                  : new JsonFileRepository(Path.Combine(root, name + ".json"));
}
