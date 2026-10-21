using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Coreの背景圧縮・同期・再起動で、ローカル履歴と現在の共有状態を混同しないことを確認します。</summary>
public sealed class HistoryArchiveProcessTests
{
    /// <summary>背景保守が自動的に進み、再起動後も元結果を読め、他Peerへローカル履歴を複製しません。</summary>
    /// <returns>二つの実Coreと実SQLiteの確認です。</returns>
    [Fact]
    public async Task BackgroundArchiveSurvivesRestartAndRemainsLocal()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string storage = Path.Combine(first.DirectoryPath, "state");
        IDurableRepository repository = await RepositoryFactory.CreateAsync(storage, "a", new(EStorageBackend.Sqlite));
        string key = Guid.NewGuid().ToString("N");
        NodeValue receipt = NodeValue.FromObject(new
        {
            Fingerprint = "fixture",
            InstanceId = Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
            Response = new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(42)),
        });
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("receipts:a", key, null, receipt)]);
        DurableEntry before = (await repository.ReadAsync("receipts:a", key))!;
        await File.AppendAllTextAsync(Path.Combine(first.DirectoryPath, "contents.toml"), """

            [storage.history]
            keepDays = 0
            maxRecords = 1
            batchRecords = 100
            """);
        await first.StartAsync();
        await CorePeer.WaitAsync(async token => 0 < (await first.GetAsync<HistoryArchiveStatus>("history.archive.status", token).ConfigureAwait(true)).ArchivedRecords);
        Assert.ThrowsAny<IOException>(() =>
        {
            using FileStream unexpected = HistoryRuntimeLease.AcquireOffline(storage);
        });
        await second.StartAsync();
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean());
        SqliteRepository secondStore = new(Path.Combine(second.DirectoryPath, "state", "state.db"));
        Assert.Empty(await secondStore.ListAsync("receipts:a"));
        Assert.Equal(ContractJson.Hash(before), ContractJson.Hash(await repository.ReadAsync("receipts:a", key)));
        await first.StopAsync();
        CorePeer restarted = new(cluster, "a", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable restartedLifetime = restarted.ConfigureAwait(true);
        await restarted.StartAsync();
        await CorePeer.WaitAsync(async token => (await restarted.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean());
        HistoryArchiveStatus status = await restarted.GetAsync<HistoryArchiveStatus>("history.archive.status");
        Assert.Null(status.Problem);
        Assert.True(0 < status.ArchivedRecords);
        IDurableRepository reopened = await RepositoryFactory.CreateAsync(storage, "a", new(EStorageBackend.Sqlite));
        Assert.Equal(ContractJson.Hash(before), ContractJson.Hash(await reopened.ReadAsync("receipts:a", key)));
        JsonElement health = await restarted.GetAsync<JsonElement>("health");
        Assert.True(health.GetProperty("ready").GetBoolean());
    }
}
