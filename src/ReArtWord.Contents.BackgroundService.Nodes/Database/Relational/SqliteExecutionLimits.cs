using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Nodes.Database.Relational;

/// <summary>同期実行するSQLiteへ、VMの取消と一行のメモリー上限を渡します。</summary>
internal static class SqliteExecutionLimits
{
    /// <summary>接続単位に制限し、別の内部SQLite接続へ影響させません。</summary>
    internal static void Configure(SqliteConnection connection, NodeSettings settings, CancellationToken cancellationToken)
    {
        SQLitePCL.raw.sqlite3_limit(connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, settings.ReadInt32("maxBytes", 16777216));
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, state =>
        {
            if (((CancellationToken)state).IsCancellationRequested)
            {
                return 1;
            }
            return 0;
        }, cancellationToken);
    }

    /// <summary>SQLiteのlock待ちを指定期限の秒精度に丸めます。</summary>
    /// <returns>0は利用者が指定した無期限です。</returns>
    internal static int Timeout(NodeSettings settings)
    {
        string timeout = "timeoutMs";
        if (settings.Get(timeout).ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return 0;
        }
        return checked((int)Math.Ceiling(settings.ReadInt32(timeout, 30000) / 1000d));
    }
}
