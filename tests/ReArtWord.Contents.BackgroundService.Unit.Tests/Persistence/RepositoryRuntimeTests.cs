using System.Text;
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

/// <summary>稼働中の接続再利用と、終了・失敗時の保存境界を実ファイルで確認します。</summary>
public sealed class RepositoryRuntimeTests
{
    /// <summary>終了後はDBだけを別保存先へ移しても、確定済みの内容を読めます。</summary>
    /// <returns>WALの反映、ファイル解放と保存結果の確認です。</returns>
    [Fact]
    public async Task ShutdownReleasesDatabaseWithCommittedValuesAsync()
    {
        using MediaTestFiles files = new();
        string database = Path.Combine(files.Root, "state.db");
        using (RepositoryRuntime runtime = await RepositoryFactory.CreateRuntimeAsync(files.Root, "a", new(EStorageBackend.Sqlite)))
        {
            Assert.Equal(EOutcome.Succeeded, await runtime.Repository.WriteLocalSnapshotAsync("peer:test", "value", NodeValue.FromObject(42)));
            Assert.Equal(42, (await runtime.Repository.ReadAsync("peer:test", "value"))!.Value!.Json.GetInt32());
            using SqliteConnection connection = Open(database);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode";
            string expectedJournal = "delete";
            if (new DriveInfo(files.Root).DriveType == DriveType.Fixed)
            {
                expectedJournal = "wal";
            }
            Assert.Equal(expectedJournal, await command.ExecuteScalarAsync().ConfigureAwait(true));
        }
        AssertReleased(database);
        Assert.False(File.Exists(database + "-wal"));
        string copy = Path.Combine(files.Root, "copy.db");
        File.Copy(database, copy);
        SqliteRepository reopened = new(copy);
        Assert.Equal(42, (await reopened.ReadAsync("peer:test", "value"))!.Value!.Json.GetInt32());
    }

    /// <summary>別接続の確定内容を、再利用中の接続でも次の取引で読みます。</summary>
    /// <returns>接続内のキャッシュに古い値を残さないことの確認です。</returns>
    [Fact]
    public async Task ReusedConnectionObservesAnotherWriterAsync()
    {
        using MediaTestFiles files = new();
        using RepositoryRuntime runtime = await RepositoryFactory.CreateRuntimeAsync(files.Root, "a", new(EStorageBackend.Sqlite));
        Assert.Equal(EOutcome.Succeeded, await runtime.Repository.WriteLocalSnapshotAsync("peer:test", "value", NodeValue.FromObject(1)));
        Assert.Equal(1, (await runtime.Repository.ReadAsync("peer:test", "value"))!.Value!.Json.GetInt32());
        SqliteRepository other = new(Path.Combine(files.Root, "state.db"));
        Assert.Equal(EOutcome.Succeeded, await other.WriteLocalSnapshotAsync("peer:test", "value", NodeValue.FromObject(2)));
        Assert.Equal(2, (await runtime.Repository.ReadAsync("peer:test", "value"))!.Value!.Json.GetInt32());
    }

    /// <summary>同じ保存先の一方の所有者が終了しても、残った所有者は保存を継続できます。</summary>
    /// <returns>プールの破棄が別所有者の取引を壊さないことの確認です。</returns>
    [Fact]
    public async Task AnotherOwnerCanContinueAfterPoolIsReleasedAsync()
    {
        using MediaTestFiles files = new();
        using RepositoryRuntime remaining = await RepositoryFactory.CreateRuntimeAsync(files.Root, "a", new(EStorageBackend.Sqlite));
        using (RepositoryRuntime first = await RepositoryFactory.CreateRuntimeAsync(files.Root, "a", new(EStorageBackend.Sqlite)))
        {
            Assert.Equal(EOutcome.Succeeded, await first.Repository.WriteLocalSnapshotAsync("peer:test", "value", NodeValue.FromObject(1)));
            Assert.Equal(1, (await remaining.Repository.ReadAsync("peer:test", "value"))!.Value!.Json.GetInt32());
        }
        Assert.Equal(EOutcome.Succeeded, await remaining.Repository.WriteLocalSnapshotAsync("peer:test", "value", NodeValue.FromObject(2)));
        Assert.Equal(2, (await remaining.Repository.ReadAsync("peer:test", "value"))!.Value!.Json.GetInt32());
    }

    /// <summary>初期化の設定不一致で公開できなかった接続も、ファイルを保持し続けません。</summary>
    /// <returns>初期化失敗後のファイル排他取得です。</returns>
    [Fact]
    public async Task FailedInitializationReleasesDatabaseAsync()
    {
        using MediaTestFiles files = new();
        _ = await RepositoryFactory.CreateAsync(files.Root, "a", new(EStorageBackend.Sqlite));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using RepositoryRuntime rejected = await RepositoryFactory.CreateRuntimeAsync(files.Root, "other", new(EStorageBackend.Sqlite)).ConfigureAwait(true);
        });
        AssertReleased(Path.Combine(files.Root, "state.db"));
    }

    /// <summary>読取り時の破損拒否後も、所有期間を終えれば接続を解放できます。</summary>
    /// <returns>例外経路のプール解放です。</returns>
    [Fact]
    public async Task FailedReadDoesNotPreventShutdownAsync()
    {
        using MediaTestFiles files = new();
        string database = Path.Combine(files.Root, "state.db");
        using (RepositoryRuntime runtime = await RepositoryFactory.CreateRuntimeAsync(files.Root, "a", new(EStorageBackend.Sqlite)))
        {
            await runtime.Repository.WriteLocalSnapshotAsync("peer:test", "value", NodeValue.FromObject(1));
            using (SqliteConnection connection = Open(database))
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE records SET payload='corrupted' WHERE kind='entry' AND scope='peer:test'";
                Assert.Equal(1, await command.ExecuteNonQueryAsync().ConfigureAwait(true));
            }
            await Assert.ThrowsAsync<InvalidDataException>(async () => await runtime.Repository.ReadAsync("peer:test", "value").ConfigureAwait(true));
        }
        AssertReleased(database);
    }

    /// <summary>SQLの変更世代が進まない実バイト破損を、接続内キャッシュで隠しません。</summary>
    /// <returns>共有版確認が実ファイルの破損を拒否することの確認です。</returns>
    [Fact]
    public async Task WarmPageCacheDoesNotHideRawPayloadCorruptionAsync()
    {
        using MediaTestFiles files = new();
        using RepositoryRuntime runtime = await RepositoryFactory.CreateRuntimeAsync(files.Root, "a", new(EStorageBackend.Sqlite));
        const string value = "original-storage-payload";
        Assert.Equal(EOutcome.Succeeded, (await runtime.Repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow", "value", null, NodeValue.FromObject(value))])).Outcome);
        string database = Path.Combine(files.Root, "state.db");
        using (SqliteConnection connection = Open(database))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            Assert.Equal(0L, await command.ExecuteScalarAsync().ConfigureAwait(true));
        }
        Assert.Equal(value, (await runtime.Repository.ReadAsync("workflow", "value"))!.Value!.GetString());
        using (FileStream file = new(database, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            byte[] bytes = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(bytes).ConfigureAwait(true);
            byte[] marker = Encoding.UTF8.GetBytes(value);
            int first = bytes.AsSpan().IndexOf(marker);
            Assert.True(100 <= first, "保存した本文がSQLiteファイルにありません。");
            int offset = 0;
            while (bytes.AsSpan(offset).IndexOf(marker) is int found && 0 <= found)
            {
                offset += found;
                file.Position = offset;
                file.WriteByte((byte)'X');
                offset += marker.Length;
            }
            await file.FlushAsync().ConfigureAwait(true);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.Repository.GetSharedVersionAsync().AsTask());
    }

    /// <summary>試験用の接続は所有期間から独立して開きます。</summary>
    /// <returns>呼出し元が解放する非プール接続です。</returns>
    private static SqliteConnection Open(string path)
        => SqliteConnectionScope.Open(path, SqliteOpenMode.ReadWrite, usePooling: false);

    /// <summary>SQLiteの接続終了を、OSの排他ファイル取得で確認します。</summary>
    private static void AssertReleased(string database)
    {
        using FileStream exclusive = new(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(0 < exclusive.Length);
    }
}
