using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>接続設定の再利用で、保存先・開始モード・プール利用が混同されないことを確認します。</summary>
public sealed class SqliteConnectionOptionsTests
{
    /// <summary>引用が必要なパスを区別し、読取り専用の接続からは書き込めません。</summary>
    [Fact]
    public void PathsAndOpenModesRemainDistinct()
    {
        using MediaTestFiles files = new();
        using SqliteConnectionLifetime lifetime = new();
        using SqliteConnectionScope scope = new(lifetime);
        string first = Path.Combine(files.Root, "first; quoted's.db");
        string second = Path.Combine(files.Root, "second.db");
        foreach ((string path, int value) in new[] { (first, 1), (second, 2) })
        {
            using SqliteConnection connection = SqliteConnectionScope.Open(path, SqliteOpenMode.ReadWriteCreate);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE sample(value INTEGER); INSERT INTO sample(value) VALUES($value)";
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
        foreach ((string path, long value) in new[] { (first, 1L), (second, 2L) })
        {
            using SqliteConnection connection = SqliteConnectionScope.Open(path, SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM sample";
            Assert.Equal(value, command.ExecuteScalar());
            command.CommandText = "INSERT INTO sample(value) VALUES(3)";
            Assert.Equal(8, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
        }
    }

    /// <summary>存在しない保存先への失敗後も、明示した作成モードへ切り替えられます。</summary>
    [Fact]
    public void FailedOpenDoesNotReuseAnotherMode()
    {
        using MediaTestFiles files = new();
        using SqliteConnectionLifetime lifetime = new();
        using SqliteConnectionScope scope = new(lifetime);
        string path = Path.Combine(files.Root, "created-later.db");
        Assert.Throws<SqliteException>(() =>
        {
            using SqliteConnection absent = SqliteConnectionScope.Open(path, SqliteOpenMode.ReadWrite);
        });
        Assert.False(File.Exists(path));
        using SqliteConnection created = SqliteConnectionScope.Open(path, SqliteOpenMode.ReadWriteCreate);
        Assert.True(File.Exists(path));
    }

    /// <summary>所有期間内でも、明示した非プール接続は終了時にファイルを解放します。</summary>
    [Fact]
    public void PoolingCanBeBypassedWithinOwnedScope()
    {
        using MediaTestFiles files = new();
        using SqliteConnectionLifetime lifetime = new();
        using SqliteConnectionScope scope = new(lifetime);
        string path = Path.Combine(files.Root, "unpooled.db");
        using (SqliteConnection connection = SqliteConnectionScope.Open(path, SqliteOpenMode.ReadWriteCreate, usePooling: false))
        {
            Assert.False(new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling);
        }
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanRead);
    }
}
