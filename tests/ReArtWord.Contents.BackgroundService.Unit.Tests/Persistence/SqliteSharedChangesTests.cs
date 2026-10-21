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
    [Fact]
    public async Task OutOfBandBlobChangesAreRecheckedWithinThirtySecondsAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("shared", "value", null, NodeValue.FromObject("test"))]).ConfigureAwait(true);
        Clock clock = new();
        SqliteCurrentVersion versions = new(path, clock);
        string before = versions.Read(CancellationToken.None);
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + path))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT rowid FROM records WHERE kind='entry' AND scope='shared'";
            long row = (long)(await command.ExecuteScalarAsync().ConfigureAwait(true))!;
            using SqliteBlob blob = new(connection, "records", "payload", row);
            blob.Position = blob.Length - 1;
            blob.WriteByte((byte)' ');
        }
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(before, versions.Read(CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidDataException>(() => versions.Read(CancellationToken.None));
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
