using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>履歴を保持しない設定でも、3年後の現在値と再実行防止・再参加を維持します。</summary>
public sealed class StateRetentionTests
{
    /// <summary>保存済み現在値を残し、過去の更新本文だけを整理します。</summary>
    /// <returns>両媒体の短期履歴、因果情報、復元後の値の検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentStateSurvivesThreeYearsWithoutRetainingChangeBodiesAsync(bool json)
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = Store(Path.Combine(files.Root, "source.db"), json);
        using HistoryArchiveService archive = new(repository);
        DataRetentionSettings policy = new(0, 0);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await archive.MaintainAsync(new(), policy, now).ConfigureAwait(true);
        Guid first = Guid.CreateVersion7(now);
        string? version = null;
        for (int index = 0; index < 120; index++)
        {
            Guid id = index == 0 ? first
                                 : Guid.CreateVersion7(now);
            IReadOnlyList<DurableMutation> mutations = await repository.StampAsync([new(NodeStateScope.Cluster, "cue", version, NodeValue.FromObject(index))]).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(id, mutations).ConfigureAwait(true)).Outcome);
            version = id.ToString("N");
        }
        using DurableStateSubscription subscription = ((IDurableStateChanges)repository).SubscribeState(NodeStateScope.Cluster, "cue");
        subscription.Initialize(await repository.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true));
        DateTimeOffset later = now.AddYears(3);
        for (int pass = 0; pass < 10; pass++)
        {
            await archive.MaintainAsync(new(BatchRecords: 1000), policy, later).ConfigureAwait(true);
        }
        Assert.Equal(119, (await repository.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal("Persistence.HistoryExpired", (await repository.ReadReceiptAsync(first).ConfigureAwait(true))!.Reason);
        RepositorySnapshot snapshot = await repository.ExportAsync().ConfigureAwait(true);
        Assert.Empty(snapshot.Receipts);
        IDurableRepository joined = Store(Path.Combine(files.Root, "joined.db"), json);
        Assert.True(await joined.ImportAsync(snapshot).ConfigureAwait(true));
        Assert.Equal(119, (await joined.ReadAsync(NodeStateScope.Cluster, "cue").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.NotEqual(EOutcome.Succeeded, (await joined.ApplyBatchAsync(first, [new(NodeStateScope.Cluster, "cue", null, NodeValue.FromObject(0))]).ConfigureAwait(true)).Outcome);
        using CancellationTokenSource cancellation = new();
        Task<DurableEntry> pending = subscription.ReadAsync(cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        await cancellation.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
    }

    /// <summary>相互に独立した永続保存先を作ります。</summary>
    /// <returns>確認対象の媒体です。</returns>
    private static IDurableRepository Store(string path, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(path);
        }
        return new SqliteRepository(path);
    }
}
