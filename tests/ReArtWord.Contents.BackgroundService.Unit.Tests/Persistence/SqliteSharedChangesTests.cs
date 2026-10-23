using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>ローカル診断で共有キャッシュを壊さず、SQL変更とSQL外破損を検出します。</summary>
public sealed class SqliteSharedChangesTests
{
    /// <summary>INSERT OR REPLACEによる共有→ローカルの移動も、その次の版確認で検出します。</summary>
    /// <returns>別接続の通常変更を見逃さない確認です。</returns>
    [Fact]
    public async Task ReplaceAndLocalSnapshotsKeepSharedInvalidationCorrectAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        string empty = await store.GetSharedVersionAsync().ConfigureAwait(true);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "value", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        string populated = await store.GetSharedVersionAsync().ConfigureAwait(true);
        Assert.NotEqual(empty, populated);
        for (int index = 0; index < 5; index++)
        {
            await store.WriteLocalSnapshotAsync("peer:p:workflow-results", "1", NodeValue.FromObject(index)).ConfigureAwait(true);
            Assert.Equal(populated, await store.GetSharedVersionAsync().ConfigureAwait(true));
        }
        using SqliteConnection connection = new("Pooling=False;Data Source=" + path);
        await connection.OpenAsync().ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO records(kind,key,scope,payload,hash) SELECT kind,key,'peer:moved',payload,hash FROM records WHERE kind='entry' AND scope='shared'";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.Equal(empty, await store.GetSharedVersionAsync().ConfigureAwait(true));
    }

    /// <summary>変更世代を通らないincremental blobの改変も、定期の本文検査で拒否します。</summary>
    /// <returns>猶予時間の境界と、破損時に確認済み版を返さない検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutOfBandBlobChangesAreRecheckedWithinThirtySecondsAsync(bool changeAnotherRow)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "value", null, NodeValue.FromObject("test")), new("shared", "counter", null, NodeValue.FromObject(0))]).ConfigureAwait(true);
        Clock clock = new();
        SqliteCurrentVersion versions = new(path, clock);
        string before = versions.Read(CancellationToken.None);
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT rowid FROM records WHERE kind='entry' AND key=$key";
            command.Parameters.AddWithValue("$key", DurableRepository.GetAddress("shared", "value"));
            long row = (long)(await command.ExecuteScalarAsync().ConfigureAwait(true))!;
            using SqliteBlob blob = new(connection, "records", "payload", row);
            blob.Position = blob.Length - 1;
            blob.WriteByte((byte)' ');
        }
        for (int index = 1; index <= 5; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            if (changeAnotherRow)
            {
                DurableEntry counter = (await store.ReadAsync("shared", "counter"))!;
                await store.ApplyBatchAsync(Guid.NewGuid(), [new(counter.Scope, counter.Key, counter.Version, NodeValue.FromObject(index))]);
            }
            before = versions.Read(CancellationToken.None);
        }
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(before, versions.Read(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidDataException>(() => versions.Read(CancellationToken.None));
    }

    /// <summary>ハッシュを変えないSQL改変やrowidを再利用した置換も、次の確認で拒否します。</summary>
    /// <returns>行の世代による即時の再検証です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlChangesCannotReuseThePreviousValidationAsync(bool replace)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "value", null, NodeValue.FromObject("test"))]);
        Assert.NotEmpty(await store.GetSharedVersionAsync());
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE records SET payload=substr(payload,1,length(payload)-1)||' ' WHERE kind='entry' AND scope='shared'";
            if (replace)
            {
                command.CommandText = "INSERT OR REPLACE INTO records(rowid,kind,key,scope,payload,hash) SELECT rowid,kind,key,scope,substr(payload,1,length(payload)-1)||' ',hash FROM records WHERE kind='entry' AND scope='shared'";
            }
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetSharedVersionAsync().AsTask());
    }

    /// <summary>旧形式へ行の世代を追加しても、本文と既存の共有版を変えません。</summary>
    /// <returns>移行後の読取りと、従来列だけを更新するwriterの検出です。</returns>
    [Fact]
    public async Task ExistingRowsKeepTheirVersionAfterMigrationAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "value", null, NodeValue.FromObject("retained"))]);
        string expected = await store.GetSharedVersionAsync();
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER shared_row_insert;
                DROP TRIGGER shared_record_update;
                DROP INDEX shared_content_lookup;
                ALTER TABLE records DROP COLUMN shared_revision;
                """;
            await command.ExecuteNonQueryAsync();
        }
        SqliteRepository migrated = new(path);
        Assert.Equal(expected, await migrated.GetSharedVersionAsync());
        DurableEntry value = (await migrated.ReadAsync("shared", "value"))!;
        Assert.Equal("retained", value.Value!.GetString());
        await migrated.ApplyBatchAsync(Guid.NewGuid(), [new(value.Scope, value.Key, value.Version, NodeValue.FromObject("changed"))]);
        Assert.NotEqual(expected, await migrated.GetSharedVersionAsync());
    }

    /// <summary>小さい共有値の変更で、未変更の大きな本文をSQLiteから読み直しません。</summary>
    /// <returns>完全検証と同じ内容版、少ないページ読取りです。</returns>
    [Fact]
    public async Task SmallChangeDoesNotRereadUnchangedLargeRowsAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "large", null, NodeValue.FromObject(new string('x', 4194304))), new("shared", "small", null, NodeValue.FromObject(0))]);
        (SqliteVersionRows cache, _, _) = Inspect(path, null, false);
        DurableEntry small = (await store.ReadAsync("shared", "small"))!;
        await store.ApplyBatchAsync(Guid.NewGuid(), [new(small.Scope, small.Key, small.Version, NodeValue.FromObject(1))]);
        (_, string full, int fullReads) = Inspect(path, cache, false);
        (_, string incremental, int incrementalReads) = Inspect(path, cache, true);
        Assert.Equal(full, incremental);
        Assert.True(512 < fullReads, "The fixture must require reading the large payload.");
        Assert.InRange(incrementalReads, 0, fullReads / 8);
    }

    /// <summary>同じ保存状態を新しい接続で確認し、OSのキャッシュによらずSQLiteの読取りページを数えます。</summary>
    /// <returns>再利用できる行、共有版、ページ読取り数です。</returns>
    private static (SqliteVersionRows Rows, string Version, int Reads) Inspect(string path, SqliteVersionRows? previous, bool reuse)
    {
        using SqliteSession session = new(path, write: false, usePooling: false);
        using SqliteCommand command = session.HistoryCommand();
        SqliteConnection connection = command.Connection!;
        Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_status(connection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out _, out _, 1));
        SqliteVersionRows? known = null;
        if (reuse)
        {
            known = previous;
        }
        SqliteSharedFingerprint.VerifiedRows verified = session.SharedFingerprint(known)!;
        (SqliteVersionRows rows, string version) = SqliteVersionRows.Read(session, verified.Rows, previous, false, false);
        Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_status(connection.Handle, SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_MISS, out int pages, out _, 0));
        return (rows, version, pages);
    }

    /// <summary>待機せず単調時計の検査境界を進めます。</summary>
    private sealed class Clock : TimeProvider
    {
        private long timestamp;

        /// <summary>TimeSpanと同じ単位を使用します。</summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>制御中の単調時刻です。</summary>
        /// <returns>検査用の経過tickです。</returns>
        public override long GetTimestamp() => timestamp;

        /// <summary>次の本文再検証までの時刻を進めます。</summary>
        internal void Advance(TimeSpan amount) => timestamp += amount.Ticks;
    }
}
