using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>保存操作の終了後にファイルを解放し、次の操作で現在の媒体を読むことを確認します。</summary>
public sealed class RoutingConnectionLifetimeTests
{
    /// <summary>一回の操作を終えた接続が、置換前のDBを保持し続けないことを確認します。</summary>
    /// <returns>ファイルの排他取得と、同じRepositoryからの置換後の値の確認です。</returns>
    [Fact]
    public async Task CompletedOperationReleasesDatabaseAndReadsReplacementAsync()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", new(EStorageBackend.Sqlite));
        Assert.Equal(EOutcome.Succeeded, await repository.WriteLocalSnapshotAsync("peer:test", "value", NodeValue.FromObject(1)));
        Assert.Equal(1, (await repository.ReadAsync("peer:test", "value"))!.Value!.Json.GetInt32());
        string database = Path.Combine(files.Root, "state.db");
        AssertReleased(database);
        string replacementPath = Path.Combine(files.Root, "replacement.db");
        File.Copy(database, replacementPath);
        SqliteRepository replacement = new(replacementPath);
        Assert.Equal(EOutcome.Succeeded, await replacement.WriteLocalSnapshotAsync("peer:test", "value", NodeValue.FromObject(2)));
        File.Move(database, Path.Combine(files.Root, "previous.db"));
        File.Move(replacementPath, database);
        Assert.Equal(2, (await repository.ReadAsync("peer:test", "value"))!.Value!.Json.GetInt32());
        AssertReleased(database);
    }

    /// <summary>保存値の破損で読取りが失敗した場合も、利用した接続を解放します。</summary>
    /// <returns>破損の拒否と、その後のファイル排他取得です。</returns>
    [Fact]
    public async Task FailedReadReleasesDatabaseAsync()
    {
        using MediaTestFiles files = new();
        IDurableRepository repository = await RepositoryFactory.CreateAsync(files.Root, "a", new(EStorageBackend.Sqlite));
        Assert.Equal(EOutcome.Succeeded, await repository.WriteLocalSnapshotAsync("peer:test", "value", NodeValue.FromObject(1)));
        string database = Path.Combine(files.Root, "state.db");
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE records SET payload='corrupted' WHERE kind='entry' AND scope='peer:test'";
            Assert.Equal(1, await command.ExecuteNonQueryAsync().ConfigureAwait(true));
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.ReadAsync("peer:test", "value").ConfigureAwait(true));
        AssertReleased(database);
    }

    /// <summary>DBを開いたままの接続がないことを、ファイル操作で確認します。</summary>
    private static void AssertReleased(string database)
    {
        using FileStream exclusive = new(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(0 < exclusive.Length);
    }
}
