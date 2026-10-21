using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>ローカル専用媒体の障害を共有メタデータ確認から分離します。</summary>
public sealed class SharedStoreIsolationTests
{
    /// <summary>ローカル専用JSONを実ロックしてもSQLiteの共有状態は確認できます。</summary>
    /// <returns>共有参照の独立性とnamed用途の障害維持です。</returns>
    [Theory]
    [InlineData(EStoragePurpose.Diagnostics, "peer:a:resources")]
    [InlineData(EStoragePurpose.Processes, "peer:a:processStates")]
    public async Task LockedPrivateOnlyJsonDoesNotBlockSharedQueries(EStoragePurpose purpose, string scope)
    {
        using MediaTestFiles files = new();
        PersistenceOptions options = new(Purposes: new Dictionary<EStoragePurpose, EStorageBackend> { [purpose] = EStorageBackend.Json });
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", options).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, await repository.WriteLocalSnapshotAsync(scope, "sample", NodeValue.FromObject(new string('x', 1048576))).ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "key", null, NodeValue.FromObject(42))]).ConfigureAwait(true)).Outcome);
        string expected = await repository.GetSharedVersionAsync().ConfigureAwait(true);
        using FileStream blocked = new(Path.Combine(files.Root, "state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(expected, await repository.GetSharedVersionAsync().ConfigureAwait(true));
        RepositorySnapshot snapshot = await repository.ExportAsync().ConfigureAwait(true);
        Assert.Equal(42, Assert.Single(snapshot.Entries).Value!.Json.GetInt32());
        Assert.Empty(await repository.GetConflictsAsync(string.Empty).ConfigureAwait(true));
        await Assert.ThrowsAsync<IOException>(async () => await repository.ReadAsync(scope, "sample").ConfigureAwait(true)).ConfigureAwait(true);
        await Assert.ThrowsAsync<IOException>(async () => await repository.GetConflictsAsync(scope).ConfigureAwait(true)).ConfigureAwait(true);
    }

    /// <summary>同じJSONでも共有用途かdefaultに割り当てた媒体を除外しません。</summary>
    /// <returns>共有内容を勝手に欠損扱いにしない検証です。</returns>
    [Theory]
    [InlineData(EStoragePurpose.Configuration, false, "workflows")]
    [InlineData(EStoragePurpose.Runtime, false, "workflow:test")]
    [InlineData(EStoragePurpose.Diagnostics, true, "unclassified-shared")]
    public async Task LockedSharedJsonStillFailsSharedQueries(EStoragePurpose purpose, bool defaultJson, string scope)
    {
        using MediaTestFiles files = new();
        PersistenceOptions options = new(Purposes: new Dictionary<EStoragePurpose, EStorageBackend> { [purpose] = EStorageBackend.Json });
        if (defaultJson)
        {
            options = new(EStorageBackend.Json, new Dictionary<EStoragePurpose, EStorageBackend> { [EStoragePurpose.Configuration] = EStorageBackend.Sqlite, [EStoragePurpose.Runtime] = EStorageBackend.Sqlite });
        }
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", options).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), [new(scope, "key", null, NodeValue.FromObject(42))]).ConfigureAwait(true)).Outcome);
        using FileStream blocked = new(Path.Combine(files.Root, "state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(async () => await repository.GetSharedVersionAsync().ConfigureAwait(true)).ConfigureAwait(true);
        await Assert.ThrowsAsync<IOException>(async () => await repository.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true);
        await Assert.ThrowsAsync<IOException>(async () => await repository.GetConflictsAsync(string.Empty).ConfigureAwait(true)).ConfigureAwait(true);
    }
}
