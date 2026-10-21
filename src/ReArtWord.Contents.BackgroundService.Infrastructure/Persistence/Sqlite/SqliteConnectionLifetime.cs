using Microsoft.Data.Sqlite;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;

/// <summary>所有期間に利用した接続プールを追跡し、終了時にファイルを解放します。</summary>
internal sealed class SqliteConnectionLifetime : IDisposable
{
    private readonly Lock gate = new();
    private readonly HashSet<string> pools = new(StringComparer.Ordinal);
    private int disposed;

    /// <summary>ローカル固定ディスクでは、確定時の同期を維持してWALを使用します。</summary>
    internal static void EnableLocalJournal(string path)
    {
        if ((OperatingSystem.IsWindows() && Path.GetPathRoot(path)!.StartsWith('\\'))
            || new DriveInfo(Path.GetDirectoryName(path)!).DriveType != DriveType.Fixed)
        {
            return;
        }
        using SqliteConnection connection = SqliteConnectionScope.Open(path, SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA journal_mode=WAL";
        string journal = "wal";
        if (!string.Equals(command.ExecuteScalar() as string, journal, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SQLiteの書込みログを有効にできません。");
        }
    }

    /// <summary>取得と終了が競合した接続も、プールへ残しません。</summary>
    internal void Track(SqliteConnection connection)
    {
        using (gate.EnterScope())
        {
            if (disposed == 0)
            {
                pools.Add(connection.ConnectionString);
                return;
            }
        }
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>この所有期間で利用した全プールを破棄します。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        string[] owned;
        using (gate.EnterScope())
        {
            owned = pools.ToArray();
            pools.Clear();
        }
        foreach (string options in owned)
        {
            using SqliteConnection connection = new(options);
            SqliteConnection.ClearPool(connection);
        }
    }
}
