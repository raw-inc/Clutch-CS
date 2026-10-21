using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Resources;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>大きい旧履歴の移行、差分保存、保存途中の失敗を実SQLiteで検証します。</summary>
public sealed class ResourceHistoryStorageTests
{
    /// <summary>旧履歴を維持して移行し、巡回後も件数と書き換え量が増大しません。</summary>
    /// <returns>復元した値と実際の保存行の検証です。</returns>
    [Fact]
    public async Task MigrationAndRingReuseKeepHistoryWithoutRewritingEverySample()
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "history.db"));
        string scope = "peer:p:resources";
        ResourceSample[] original = Enumerable.Range(0, 720).Select(index => Sample(index)).ToArray();
        await repository.WriteLocalSnapshotAsync(scope, "history", NodeValue.FromObject(original));
        ResourceHistoryStore store = new(repository, scope);
        Assert.Equal(original.Select(sample => sample.Id), (await store.ReadAsync()).Select(sample => sample.Id));
        Assert.True(await store.WriteAsync(original));
        Dictionary<string, string> versions = (await repository.ListAsync(scope)).ToDictionary(entry => entry.Key, entry => entry.Version);
        List<ResourceSample> expected = original.ToList();
        for (int iteration = 0; iteration < 100; iteration++)
        {
            expected.AddRange(Enumerable.Range(0, 10).Select(index => Sample(iteration * 10 + index + 720)));
            expected = expected.TakeLast(720).ToList();
            Assert.True(await store.WriteAsync(expected.ToArray()));
            IReadOnlyList<DurableEntry> entries = await repository.ListAsync(scope);
            DurableEntry[] changed = entries.Where(entry => !string.Equals(versions.GetValueOrDefault(entry.Key), entry.Version, StringComparison.Ordinal)).ToArray();
            Assert.True(changed.Length <= 4);
            Assert.True(entries.Count <= 28);
            versions = entries.ToDictionary(entry => entry.Key, entry => entry.Version);
        }
        ResourceHistoryStore restarted = new(repository, scope);
        Assert.Equal(expected.Select(sample => sample.Id), (await restarted.ReadAsync()).Select(sample => sample.Id));
        Assert.Empty((await repository.ReadAsync(scope, "history"))!.Value!.Json.EnumerateArray());
    }

    /// <summary>新しい区間を書いた後に索引保存が失敗しても、最後の確定履歴を復元できます。</summary>
    /// <returns>失敗前の復元と同じサービスによる再保存の検証です。</returns>
    [Fact]
    public async Task IndexFailureLeavesPreviousHistoryReadableAndCanBeRetried()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "history.db");
        SqliteRepository repository = new(path);
        string scope = "peer:p:resources";
        ResourceHistoryStore store = new(repository, scope);
        await store.ReadAsync();
        ResourceSample[] original = Enumerable.Range(0, 70).Select(Sample).ToArray();
        Assert.True(await store.WriteAsync(original));
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        using SqliteCommand fail = connection.CreateCommand();
        fail.CommandText = "CREATE TRIGGER fail_index BEFORE INSERT ON records WHEN json_extract(NEW.key, '$[1]') = 'history.index' BEGIN SELECT RAISE(FAIL, 'test failure'); END;";
        await fail.ExecuteNonQueryAsync();
        ResourceSample[] changed = original.Skip(10).Concat(Enumerable.Range(70, 10).Select(Sample)).ToArray();
        await Assert.ThrowsAsync<SqliteException>(() => store.WriteAsync(changed));
        Assert.Equal(original.Select(sample => sample.Id), (await new ResourceHistoryStore(repository, scope).ReadAsync()).Select(sample => sample.Id));
        fail.CommandText = "DROP TRIGGER fail_index";
        await fail.ExecuteNonQueryAsync();
        Assert.True(await store.WriteAsync(changed));
        Assert.Equal(changed.Select(sample => sample.Id), (await new ResourceHistoryStore(repository, scope).ReadAsync()).Select(sample => sample.Id));
    }

    /// <summary>索引確定後の旧本文除去だけが失敗しても、再起動後に新形式を確認して除去を再試行します。</summary>
    /// <returns>元本文の保護と、処理済み移行を取り残さないことの確認です。</returns>
    [Fact]
    public async Task LegacyCleanupCanResumeAfterIndexWasCommittedAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "history.db"), scope = "peer:p:resources";
        SqliteRepository repository = new(path);
        ResourceSample[] samples = Enumerable.Range(0, 40).Select(Sample).ToArray();
        await repository.WriteLocalSnapshotAsync(scope, "history", NodeValue.FromObject(samples));
        ResourceHistoryStore store = new(repository, scope);
        await store.ReadAsync();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_cleanup BEFORE INSERT ON records WHEN json_extract(NEW.key,'$[1]')='history' BEGIN SELECT RAISE(FAIL,'test failure'); END";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => store.WriteAsync(samples));
        Assert.NotNull(await repository.ReadAsync(scope, "history.index"));
        Assert.Equal(samples.Length, (await repository.ReadAsync(scope, "history"))!.Value!.Json.GetArrayLength());
        command.CommandText = "DROP TRIGGER fail_cleanup";
        await command.ExecuteNonQueryAsync();
        ResourceHistoryStore restarted = new(repository, scope);
        Assert.Equal(samples.Select(sample => sample.Id), (await restarted.ReadAsync()).Select(sample => sample.Id));
        Assert.True(await restarted.WriteAsync(samples));
        Assert.Equal(0, (await repository.ReadAsync(scope, "history"))!.Value!.Json.GetArrayLength());
    }

    /// <summary>実データ量を持つ一件の診断を作ります。</summary>
    /// <returns>順序と内容を識別できる観測です。</returns>
    private static ResourceSample Sample(int number) => new(Guid.NewGuid(), "p", DateTimeOffset.UtcNow.AddMilliseconds(number), [new("cpu.usage", "machine", number, "percent", new string('x', 1024))], true);
}
