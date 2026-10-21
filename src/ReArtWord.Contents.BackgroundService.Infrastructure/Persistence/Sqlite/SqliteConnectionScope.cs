using Microsoft.Data.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>一回の排他処理で利用する接続を、指定された所有期間へ登録します。</summary>
internal sealed class SqliteConnectionScope : IDisposable
{
    private static readonly AsyncLocal<SqliteConnectionScope?> current = new();
    private readonly SqliteConnectionScope? previous;
    private readonly SqliteConnectionLifetime lifetime;
    private readonly bool ownsLifetime;
    private int disposed;

    /// <summary>呼出し元の非同期処理に、この区間の接続管理を設定します。</summary>
    internal SqliteConnectionScope(SqliteConnectionLifetime? lifetime = null)
    {
        this.lifetime = lifetime ?? new();
        ownsLifetime = lifetime is null;
        previous = current.Value;
        current.Value = this;
    }

    /// <summary>排他区間の外では従来どおり接続を直ちに閉じます。</summary>
    private static SqliteConnectionScope? Current => current.Value is { } scope && Volatile.Read(ref scope.disposed) == 0 ? scope
                                                                                                                          : null;

    /// <summary>業務処理前のメタデータ読取りで、一時的なWindowsの共有メモリ障害だけを回復します。</summary>
    /// <returns>呼出し元が破棄する接続です。</returns>
    internal static SqliteConnection Open(string path, SqliteOpenMode mode, bool usePooling = true)
    {
        try
        {
            return OpenCore(path, mode, usePooling);
        }
        catch (SqliteException error) when (OperatingSystem.IsWindows() && error.SqliteExtendedErrorCode == SQLitePCL.raw.SQLITE_IOERR_TRUNCATE)
        {
            // WAL初期化時に残ったマッピングの解放を確認するため、接続を一度だけ作り直します。
            return OpenCore(path, mode, usePooling);
        }
    }

    /// <summary>メタデータを読めることを確認してから、接続を呼出し元へ渡します。</summary>
    /// <returns>呼出し元が破棄する接続です。</returns>
    // 抑制理由: 区間は呼出側の所有です。返却する接続は所有権を移し、取得失敗時だけfinallyで破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "区間と返却接続は呼出側が破棄し、取得失敗時の接続はfinallyのusingで解放します。")]
    private static SqliteConnection OpenCore(string path, SqliteOpenMode mode, bool usePooling)
    {
        SqliteConnectionScope? scope = usePooling ? Current
                                                  : null;
        SqliteConnection? connection = null;
        try
        {
            connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = scope is not null }.ToString());
            try
            {
                connection.Open();
                using SqliteCommand schema = connection.CreateCommand();
                schema.CommandText = "PRAGMA schema_version";
                schema.ExecuteNonQuery();
            }
            finally
            {
                scope?.lifetime.Track(connection);
            }
            SqliteConnection result = connection;
            connection = null;
            return result;
        }
        finally
        {
            if (connection is not null)
            {
                SqliteConnection.ClearPool(connection);
            }
            using SqliteConnection? abandoned = connection;
        }
    }

    /// <summary>稼働期間が指定されていない場合は、排他区間の終了時にプールを破棄します。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        if (ReferenceEquals(current.Value, this))
        {
            current.Value = previous;
        }
        if (ownsLifetime)
        {
            lifetime.Dispose();
        }
    }
}
